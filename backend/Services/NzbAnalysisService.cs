using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public class AnalysisInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public int Progress { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class NzbAnalysisService(
    IServiceScopeFactory scopeFactory,
    UsenetStreamingClient usenetClient,
    WebsocketManager websocketManager,
    ConfigManager configManager,
    MediaAnalysisService mediaAnalysisService
)
{
    private static readonly ConcurrentDictionary<Guid, AnalysisInfo> _activeAnalyses = new();
    private static readonly ConcurrentDictionary<Guid, int> _ffprobeRetryAttempts = new();
    private readonly SemaphoreSlim _concurrencyLimiter = new(configManager.GetMaxConcurrentAnalyses(), configManager.GetMaxConcurrentAnalyses());

    public IEnumerable<AnalysisInfo> GetActiveAnalyses() => _activeAnalyses.Values;

    public void TriggerAnalysisInBackground(Guid fileId, string[]? segmentIds, bool force = false)
    {
        if (!force && !configManager.IsAnalysisEnabled()) return;
        if (_activeAnalyses.ContainsKey(fileId)) return;
        _ = Task.Run(async () => await PerformAnalysis(fileId, segmentIds, force).ConfigureAwait(false));
    }

    private async Task PerformAnalysis(Guid fileId, string[]? segmentIds, bool force = false)
    {
        var info = new AnalysisInfo { Id = fileId, Name = "Queued" };
        if (!_activeAnalyses.TryAdd(fileId, info)) return;

        // Wait for available analysis slot
        await _concurrencyLimiter.WaitAsync().ConfigureAwait(false);

        try
        {
            Log.Debug("[NzbAnalysisService] Starting background analysis for file {Id} (Force={Force})", fileId, force);

            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            var davItem = await dbContext.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId).ConfigureAwait(false);
            if (davItem == null) return;

            var nzbFile = await dbContext.NzbFiles.FirstOrDefaultAsync(f => f.Id == fileId).ConfigureAwait(false);

            // Update name in tracking info
            info.Name = davItem.Name;
            
            // Try to extract Job Name (Parent Directory Name)
            if (davItem.Path != null)
            {
                // Path format: /.../Category/JobName/Filename.ext
                var directoryName = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
                if (!string.IsNullOrEmpty(directoryName))
                {
                    info.JobName = directoryName;
                }
            }
            
            Log.Information("[NzbAnalysisService] Starting analysis for file: {FileName} ({Id})", info.Name, fileId);
            
            // Broadcast initial state with JobName
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|start|{info.Name}|{info.JobName}");

            var segmentAnalysisComplete = nzbFile == null || nzbFile.SegmentSizes != null;
            var mediaAnalysisComplete = davItem.MediaInfo != null;

            if (!force && segmentAnalysisComplete && mediaAnalysisComplete)
            {
                Log.Information("[NzbAnalysisService] Analysis already complete for file: {FileName} ({Id})", info.Name, fileId);
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|100");
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
                return;
            }

            // Create cancellation token with usage context so analysis operations show up in stats
            using var cts = new CancellationTokenSource();
            // Normalize AffinityKey from parent directory (matches WebDav file patterns)
            var rawAffinityKey = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
            var normalizedAffinityKey = FilenameNormalizer.NormalizeName(rawAffinityKey);
            var usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Analysis,
                new ConnectionUsageDetails { Text = davItem.Path, JobName = davItem.Name, AffinityKey = normalizedAffinityKey, DavItemId = davItem.Id }
            );
            using var _ = cts.Token.SetScopedContext(usageContext);

            if (nzbFile != null && (force || nzbFile.SegmentSizes == null) && segmentIds != null)
            {
                var progressHook = new Progress<int>();
                var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));
                progressHook.ProgressChanged += (_, count) =>
                {
                    // Scale NZB analysis to 90%
                    var percentage = (int)((double)count / segmentIds.Length * 90);
                    if (percentage > info.Progress)
                    {
                        info.Progress = percentage;
                        debounce(() => websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|{percentage}"));
                    }
                };

                var sizes = await usenetClient.AnalyzeNzbAsync(segmentIds, 10, progressHook, cts.Token).ConfigureAwait(false);

                nzbFile.SetSegmentSizes(sizes);
                dbContext.NzbFiles.Update(nzbFile);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            // Media Analysis (ffprobe)
            var mediaResult = MediaAnalysisResult.Success;
            if (force || !mediaAnalysisComplete)
            {
                info.Progress = 90;
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|90");
                mediaResult = await mediaAnalysisService.AnalyzeMediaAsync(fileId, cts.Token).ConfigureAwait(false);
            }

            // Handle ffprobe timeout - schedule retry if we haven't already retried
            if (mediaResult == MediaAnalysisResult.Timeout)
            {
                var retryCount = _ffprobeRetryAttempts.GetOrAdd(fileId, 0);
                if (retryCount < 1)
                {
                    _ffprobeRetryAttempts[fileId] = retryCount + 1;
                    Log.Warning("[NzbAnalysisService] ffprobe timed out for {FileName}. Scheduling retry in 1 hour (attempt {Attempt}/1)", info.Name, retryCount + 1);

                    // Schedule retry in 1 hour (fire-and-forget)
                    var retryTask = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromHours(1)).ConfigureAwait(false);
                        Log.Information("[NzbAnalysisService] Executing scheduled ffprobe retry for {FileName} ({Id})", info.Name, fileId);
                        TriggerAnalysisInBackground(fileId, segmentIds, force: true);
                    });
                    // Suppress warning - intentional fire-and-forget
                    GC.KeepAlive(retryTask);

                    websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|90");
                    websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|pending");
                    await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Pending", "ffprobe timed out - retry scheduled in 1 hour").ConfigureAwait(false);
                    return;
                }
                else
                {
                    // Already retried once, mark as failed
                    Log.Warning("[NzbAnalysisService] ffprobe timed out again for {FileName}. Max retries reached.", info.Name);
                    _ffprobeRetryAttempts.TryRemove(fileId, out var unused1);
                    websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
                    await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Failed", "ffprobe timed out after retry").ConfigureAwait(false);
                    return;
                }
            }

            // Clear retry counter on success
            _ffprobeRetryAttempts.TryRemove(fileId, out var unused2);

            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|100");
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
            Log.Information("[NzbAnalysisService] Finished analysis for file: {FileName} ({Id})", info.Name, fileId);

            await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Success", "Analysis completed successfully").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbAnalysisService] Failed to analyze file {Id}", fileId);
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
            await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Failed", ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _activeAnalyses.TryRemove(fileId, out _);
            _concurrencyLimiter.Release();
        }
    }

    private async Task SaveAnalysisHistoryAsync(Guid davItemId, string fileName, string jobName, string result, string details)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            
            var item = new AnalysisHistoryItem
            {
                DavItemId = davItemId,
                FileName = fileName,
                JobName = jobName,
                Result = result,
                Details = details,
                CreatedAt = DateTimeOffset.UtcNow
            };
            
            db.AnalysisHistoryItems.Add(item);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbAnalysisService] Failed to save analysis history for {FileName}", fileName);
        }
    }
}