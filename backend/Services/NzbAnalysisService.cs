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
    ConfigManager configManager
)
{
    private static readonly ConcurrentDictionary<Guid, AnalysisInfo> _activeAnalyses = new();
    private readonly SemaphoreSlim _concurrencyLimiter = new(configManager.GetMaxConcurrentAnalyses(), configManager.GetMaxConcurrentAnalyses());

    public IEnumerable<AnalysisInfo> GetActiveAnalyses() => _activeAnalyses.Values;

    public void TriggerAnalysisInBackground(Guid fileId, string[] segmentIds, bool force = false)
    {
        if (!force && !configManager.IsAnalysisEnabled()) return;
        if (_activeAnalyses.ContainsKey(fileId)) return;
        _ = Task.Run(async () => await PerformAnalysis(fileId, segmentIds).ConfigureAwait(false));
    }
// ...

    private async Task PerformAnalysis(Guid fileId, string[] segmentIds)
    {
        var info = new AnalysisInfo { Id = fileId, Name = "Queued" };
        if (!_activeAnalyses.TryAdd(fileId, info)) return;

        // Wait for available analysis slot
        await _concurrencyLimiter.WaitAsync().ConfigureAwait(false);

        try
        {
            Log.Debug("[NzbAnalysisService] Starting background analysis for file {Id}", fileId);

            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            var file = await dbContext.NzbFiles
                .AsNoTracking()
                .Include(f => f.DavItem)
                .FirstOrDefaultAsync(f => f.Id == fileId)
                .ConfigureAwait(false);
            
            if (file == null) return;

            // Update name in tracking info
            info.Name = file.DavItem?.Name ?? fileId.ToString();
            
            // Try to extract Job Name (Parent Directory Name)
            if (file.DavItem?.Path != null)
            {
                // Path format: /.../Category/JobName/Filename.ext
                // OR /.../Category/JobName.ext
                // We want the parent directory name generally
                var directoryName = Path.GetFileName(Path.GetDirectoryName(file.DavItem.Path));
                if (!string.IsNullOrEmpty(directoryName))
                {
                    info.JobName = directoryName;
                }
            }
            
            Log.Information("[NzbAnalysisService] Starting analysis for file: {FileName} ({Id})", info.Name, fileId);
            
            // Broadcast initial state with JobName
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|start|{info.Name}|{info.JobName}");

            if (file.SegmentSizes != null)
            {
                Log.Information("[NzbAnalysisService] Analysis already complete for file: {FileName} ({Id})", info.Name, fileId);
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|100");
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
                return;
            }

            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));
            progressHook.ProgressChanged += (_, count) =>
            {
                var percentage = (int)((double)count / segmentIds.Length * 100);
                if (percentage > info.Progress)
                {
                    info.Progress = percentage;
                    debounce(() => websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|{percentage}"));
                }
            };

            // Create cancellation token with usage context so analysis operations show up in stats
            using var cts = new CancellationTokenSource();
            var usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Analysis,
                new ConnectionUsageDetails { Text = file.DavItem?.Path ?? info.Name }
            );
            using var _ = cts.Token.SetScopedContext(usageContext);

            var sizes = await usenetClient.AnalyzeNzbAsync(segmentIds, 10, progressHook, cts.Token).ConfigureAwait(false);

            file.SetSegmentSizes(sizes);
            dbContext.NzbFiles.Update(file);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|100");
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
            Log.Information("[NzbAnalysisService] Finished analysis for file: {FileName} ({Id})", info.Name, fileId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbAnalysisService] Failed to analyze NZB {Id}", fileId);
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
        }
        finally
        {
            _activeAnalyses.TryRemove(fileId, out _);
            _concurrencyLimiter.Release();
        }
    }
}