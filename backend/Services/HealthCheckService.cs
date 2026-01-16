using NzbWebDAV.Clients.RadarrSonarr;
using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// This service monitors for health checks
/// </summary>
public class HealthCheckService
{
    private readonly ConfigManager _configManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly WebsocketManager _websocketManager;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ProviderErrorService _providerErrorService;
    private readonly NzbAnalysisService _nzbAnalysisService;
    private readonly CancellationToken _cancellationToken = SigtermUtil.GetCancellationToken();

    private readonly ConcurrentDictionary<string, byte> _missingSegmentIds = new();
    private readonly ConcurrentDictionary<Guid, int> _timeoutCounts = new();
    private readonly SemaphoreSlim _concurrencyLimiter = new(1);
    private readonly ConcurrentDictionary<Guid, byte> _processingIds = new();

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager,
        IServiceScopeFactory serviceScopeFactory,
        ProviderErrorService providerErrorService,
        NzbAnalysisService nzbAnalysisService
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;
        _serviceScopeFactory = serviceScopeFactory;
        _providerErrorService = providerErrorService;
        _nzbAnalysisService = nzbAnalysisService;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // when usenet host changes, clear the missing segments cache
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.host")) return;
            _missingSegmentIds.Clear();
        };

        _ = StartMonitoringService();
    }

    private async Task StartMonitoringService()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Wait for a concurrency slot
                await _concurrencyLimiter.WaitAsync(_cancellationToken).ConfigureAwait(false);

                // Find next candidate
                DavItem? candidate = null;
                try
                {
                    await using var dbContext = new DavDatabaseContext();
                    var dbClient = new DavDatabaseClient(dbContext);
                    var currentDateTime = DateTimeOffset.UtcNow;
                    
                    // Fetch a few candidates to skip over ones currently being processed
                    var candidates = await GetHealthCheckQueueItems(dbClient)
                        .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime)
                        .Take(10)
                        .ToListAsync(_cancellationToken).ConfigureAwait(false);

                    foreach (var item in candidates)
                    {
                        if (_processingIds.TryAdd(item.Id, 0))
                        {
                            candidate = item;
                            break;
                        }
                    }
                }
                catch
                {
                    // If DB fetch fails, release slot and wait
                    _concurrencyLimiter.Release();
                    throw;
                }

                if (candidate == null)
                {
                    _concurrencyLimiter.Release();
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Spawn background task
                _ = ProcessItemInBackground(candidate);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown
            }
            catch (Exception e)
            {
                Log.Error(e, "Error in HealthCheck monitoring loop");
                await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessItemInBackground(DavItem itemInfo)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var dbClient = new DavDatabaseClient(dbContext);

            // Re-fetch item to attach to current context
            var davItem = await dbContext.Items
                .FirstOrDefaultAsync(x => x.Id == itemInfo.Id, _cancellationToken)
                .ConfigureAwait(false);

            if (davItem == null) return;

            var concurrency = _configManager.GetMaxRepairConnections();

            // set connection usage context
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
            var timeoutMinutes = 30;
            cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes)); // Timeout after 30 minutes per file
            
            // Normalize AffinityKey from parent directory (matches WebDav file patterns)
            var rawAffinityKey = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
            var normalizedAffinityKey = FilenameNormalizer.NormalizeName(rawAffinityKey);
            using var contextScope = cts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.HealthCheck, new ConnectionUsageDetails { Text = "Health Check", JobName = davItem.Name, AffinityKey = normalizedAffinityKey, DavItemId = davItem.Id }));

            // Determine if this is an urgent health check
            var isUrgentCheck = davItem.NextHealthCheck == DateTimeOffset.MinValue;
            var useHead = isUrgentCheck;

            Log.Information("[HealthCheck] Processing item: {Name} ({Id}). Type: {Type}. Timeout: {Timeout}m",
                davItem.Name, davItem.Id, isUrgentCheck ? "Urgent (HEAD)" : "Routine (STAT)", timeoutMinutes);
            
            await PerformHealthCheck(davItem, dbClient, concurrency, cts.Token, useHead).ConfigureAwait(false);

            // Success! Remove from timeout tracking
            _timeoutCounts.TryRemove(davItem.Id, out _);

            // Reload to get the latest state after health check
            await dbClient.Ctx.Entry(davItem).ReloadAsync(cts.Token).ConfigureAwait(false);
            var result = davItem.IsCorrupted ? "Unhealthy (Repair Attempted)" : "Healthy";
            Log.Information("[HealthCheck] Finished item: {Name}. Result: {Result}", davItem.Name, result);
        }
        catch (OperationCanceledException) when (!_cancellationToken.IsCancellationRequested)
        {
            // Handle per-item timeout
            await HandleTimeout(itemInfo.Id, itemInfo.Name, itemInfo.Path, itemInfo.NextHealthCheck == DateTimeOffset.MinValue);
        }
        catch (Exception e)
        {
            var isTimeout = e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
            if (isTimeout)
            {
                Log.Error($"[HealthCheck] Unexpected error processing {itemInfo.Name}: {e.Message}");
            }
            else
            {
                Log.Error(e, $"[HealthCheck] Unexpected error processing {itemInfo.Name}");
            }

            try
            {
                // Mark as failed in DB to prevent infinite loops and allow filtering
                await using var errorContext = new DavDatabaseContext();
                var utcNow = DateTimeOffset.UtcNow;
                
                await errorContext.Items
                    .Where(x => x.Id == itemInfo.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.NextHealthCheck, utcNow.AddDays(1)) // Retry in 24h
                        .SetProperty(p => p.LastHealthCheck, utcNow)
                        .SetProperty(p => p.IsCorrupted, true)
                        .SetProperty(p => p.CorruptionReason, e.Message))
                    .ConfigureAwait(false);

                errorContext.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = itemInfo.Id,
                    Path = itemInfo.Path,
                    CreatedAt = utcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                    Message = $"Unexpected error: {e.Message}"
                }));
                await errorContext.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception dbEx)
            {
                Log.Error(dbEx, "[HealthCheck] Failed to save error status to database.");
            }
        }
        finally
        {
            _processingIds.TryRemove(itemInfo.Id, out _);
            _concurrencyLimiter.Release();
        }
    }

    private async Task HandleTimeout(Guid itemId, string name, string path, bool wasUrgent)
    {
        var timeouts = _timeoutCounts.AddOrUpdate(itemId, 1, (_, count) => count + 1);

        if (timeouts >= 2)
        {
            Log.Error("[HealthCheck] Item {Name} timed out {Timeouts} times. Marking as failed.", name, timeouts);
            _timeoutCounts.TryRemove(itemId, out _);

            try
            {
                await using var dbContext = new DavDatabaseContext();

                // Update the item to prevent immediate retry loop
                var utcNow = DateTimeOffset.UtcNow;
                var nextCheck = utcNow.AddDays(1);
                await dbContext.Items
                    .Where(x => x.Id == itemId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.NextHealthCheck, nextCheck)
                        .SetProperty(p => p.LastHealthCheck, utcNow))
                    .ConfigureAwait(false);

                dbContext.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = itemId,
                    Path = path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                    Message = "Health check timed out repeatedly (likely due to slow download or hanging)."
                }));
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[HealthCheck] Failed to mark timed-out item as failed.");
            }
        }
        else
        {
            Log.Warning("[HealthCheck] Timed out processing item: {Name}. Rescheduling for later (Attempt {Timeouts}).", name, timeouts);
            
            if (!wasUrgent)
            {
                try
                {
                    await using var dbContext = new DavDatabaseContext();
                    var nextCheck = DateTimeOffset.UtcNow.AddHours(1);
                    await dbContext.Items
                        .Where(x => x.Id == itemId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.NextHealthCheck, nextCheck))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[HealthCheck] Failed to reschedule timed-out item.");
                }
            }
            else
            {
                Log.Warning($"[HealthCheck] Item `{name}` timed out during Urgent check. Keeping priority high for immediate retry.");
            }
        }
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        return dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => x.Type == DavItem.ItemType.NzbFile
                        || x.Type == DavItem.ItemType.RarFile
                        || x.Type == DavItem.ItemType.MultipartFile);
    }

    private async Task PerformHealthCheck
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct,
        bool useHead
    )
    {
        List<string> segments = [];
        try
        {
            // Check if item is mapped (exists in LocalLinks table)
            var isMapped = await dbClient.Ctx.LocalLinks.AnyAsync(x => x.DavItemId == davItem.Id, ct).ConfigureAwait(false);
            if (!isMapped)
            {
                Log.Warning("[HealthCheck] Item {Name} ({Id}) is not mapped (not found in LocalLinks). Skipping health check.", davItem.Name, davItem.Id);

                davItem.LastHealthCheck = DateTimeOffset.UtcNow;
                davItem.NextHealthCheck = davItem.LastHealthCheck.Value.AddDays(7);
                davItem.IsCorrupted = false;
                davItem.CorruptionReason = null;

                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Skipped,
                    RepairStatus = HealthCheckResult.RepairAction.None,
                    Message = "Not mapped"
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // update the release date, if null
            segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            Log.Debug($"[HealthCheck] Fetched {segments.Count} segments for {davItem.Name}");

            if (segments.Count == 0)
            {
                Log.Warning("[HealthCheck] Item {Name} ({Id}) has no segments found. Skipping health check.", davItem.Name, davItem.Id);

                davItem.LastHealthCheck = DateTimeOffset.UtcNow;
                davItem.NextHealthCheck = davItem.LastHealthCheck.Value.AddDays(7);
                davItem.IsCorrupted = false;
                davItem.CorruptionReason = null;

                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Skipped,
                    RepairStatus = HealthCheckResult.RepairAction.None,
                    Message = "No segments found"
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            if (davItem.ReleaseDate == null) await UpdateReleaseDate(davItem, segments, ct).ConfigureAwait(false);

            // Trigger analysis if media info is missing (ffprobe check)
            if (davItem.MediaInfo == null)
            {
                _nzbAnalysisService.TriggerAnalysisInBackground(davItem.Id, segments.ToArray());
            }

            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
            progressHook.ProgressChanged += (_, progress) =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // perform health check
            Log.Debug($"[HealthCheck] Verifying segments for {davItem.Name} using {(useHead ? "HEAD" : "STAT")}...");
            var progress = progressHook.ToPercentage(segments.Count);
            var isImported = OrganizedLinksUtil.GetLink(davItem, _configManager, allowScan: false) != null;
            using var healthCheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Normalize AffinityKey from parent directory (matches WebDav file patterns)
            var rawAffinityKey2 = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
            var normalizedAffinityKey2 = FilenameNormalizer.NormalizeName(rawAffinityKey2);
            using var contextScope = healthCheckCts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.HealthCheck, new ConnectionUsageDetails { Text = davItem.Path, JobName = davItem.Name, AffinityKey = normalizedAffinityKey2, IsImported = isImported, DavItemId = davItem.Id }));
            var sizes = await _usenetClient.CheckAllSegmentsAsync(segments, concurrency, progress, healthCheckCts.Token, useHead).ConfigureAwait(false);

            // If we did a HEAD check, we now have the segment sizes. Cache them for faster seeking.
            if (useHead && sizes != null && davItem.Type == DavItem.ItemType.NzbFile)
            {
                var nzbFile = await dbClient.GetNzbFileAsync(davItem.Id, ct).ConfigureAwait(false);
                if (nzbFile != null)
                {
                    nzbFile.SetSegmentSizes(sizes);
                    Log.Debug($"[HealthCheck] Cached {sizes.Length} segment sizes for {davItem.Name}");
                }
            }

            Log.Debug($"[HealthCheck] Segments verified for {davItem.Name}. Updating database...");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            // Prevent Race Condition:
            // If this was a Routine (STAT) check, but the file was marked Urgent (HEAD) by the middleware 
            // *during* this check (e.g. user tried to stream and failed), we must NOT overwrite the Urgent status.
            if (!useHead)
            {
                await dbClient.Ctx.Entry(davItem).ReloadAsync(ct).ConfigureAwait(false);
                if (davItem.NextHealthCheck == DateTimeOffset.MinValue)
                {
                    Log.Warning($"[HealthCheck] Item `{davItem.Name}` was marked Urgent during a Routine check. Aborting save to allow Immediate Urgent (HEAD) check.");
                    return;
                }
            }

            // update the database
            davItem.LastHealthCheck = DateTimeOffset.UtcNow;

            // Calculate next health check with configurable minimum interval
            // This accounts for local filesystem caching - new files don't need frequent checks
            // Note: Priority/triggered checks (NextHealthCheck = MinValue) bypass this logic
            var age = davItem.LastHealthCheck - davItem.ReleaseDate;
            var interval = age; // Exponential backoff: interval = age
            var minIntervalDays = _configManager.GetMinHealthCheckIntervalDays();
            var minInterval = TimeSpan.FromDays(minIntervalDays);

            // Ensure minimum interval between checks (configurable, default 7 days)
            if (interval < minInterval)
                interval = minInterval;

            davItem.NextHealthCheck = davItem.LastHealthCheck + interval;
            davItem.IsCorrupted = false;
            davItem.CorruptionReason = null;
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Healthy,
                RepairStatus = HealthCheckResult.RepairAction.None,
                Message = "File is healthy."
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            var totalSegments = segments.Count;
            var missingIndex = segments.IndexOf(e.SegmentId);
            var percentage = totalSegments > 0 ? (double)missingIndex / totalSegments * 100.0 : 0;
            var failureDetails = $"Missing segment at index {missingIndex}/{totalSegments} ({percentage:F2}%)";

            Log.Warning("[HealthCheck] Health check failed for item {Name} (Missing Segment: {SegmentId}). {FailureDetails}. Attempting repair.",
                davItem.Name, e.SegmentId, failureDetails);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            if (FilenameUtil.IsImportantFileType(davItem.Name))
                _missingSegmentIds.TryAdd(e.SegmentId, 0);

            // when usenet article is missing, perform repairs
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Normalize AffinityKey from parent directory (matches WebDav file patterns)
            var rawAffinityKey3 = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
            var normalizedAffinityKey3 = FilenameNormalizer.NormalizeName(rawAffinityKey3);
            using var _3 = cts2.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Repair, new ConnectionUsageDetails { Text = davItem.Path, JobName = davItem.Name, AffinityKey = normalizedAffinityKey3, DavItemId = davItem.Id }));

            // Set operation type based on the check method used
            var operation = useHead ? "HEAD" : "STAT";

            // Call the Repair method which handles all arr clients properly
            await Repair(davItem, dbClient, cts2.Token, failureDetails, operation).ConfigureAwait(false);
        }
    }

    private async Task UpdateReleaseDate(DavItem davItem, List<string> segments, CancellationToken ct)
    {
        var firstSegmentId = StringUtil.EmptyToNull(segments.FirstOrDefault());
        if (firstSegmentId == null) return;
        var articleHeaders = await _usenetClient.GetArticleHeadersAsync(firstSegmentId, ct).ConfigureAwait(false);
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private async Task<List<string>> GetAllSegments(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.Type == DavItem.ItemType.NzbFile)
        {
            var nzbFile = await dbClient.GetNzbFileAsync(davItem.Id, ct).ConfigureAwait(false);
            return nzbFile?.SegmentIds?.ToList() ?? [];
        }

        if (davItem.Type == DavItem.ItemType.RarFile)
        {
            var rarFile = await dbClient.Ctx.RarFiles
                .AsNoTracking()
                .Where(x => x.Id == davItem.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        if (davItem.Type == DavItem.ItemType.MultipartFile)
        {
            var multipartFile = await dbClient.Ctx.MultipartFiles
                .AsNoTracking()
                .Where(x => x.Id == davItem.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        return [];
    }

    public void TriggerManualRepairInBackground(string filePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                
                await TriggerManualRepairAsync(filePath, dbClient, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[ManualRepair] Failed to execute manual repair for file: {filePath}");
            }
        });
    }

    public async Task TriggerManualRepairAsync(string filePath, DavDatabaseClient dbClient, CancellationToken ct)
    {
        Log.Information("Manual repair triggered for file: {FilePath}", filePath);

        // 1. Try exact match
        var davItem = await dbClient.Ctx.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Path == filePath, ct).ConfigureAwait(false);

        // 2. Try unescaped match
        if (davItem == null)
        {
            var unescapedPath = Uri.UnescapeDataString(filePath);
            if (unescapedPath != filePath)
            {
                davItem = await dbClient.Ctx.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Path == unescapedPath, ct).ConfigureAwait(false);
            }
        }

        // 3. Try match by filename (if unique)
        if (davItem == null)
        {
            var fileName = Path.GetFileName(filePath);
            var candidates = await dbClient.Ctx.Items.AsNoTracking().Where(x => x.Name == fileName).ToListAsync(ct).ConfigureAwait(false);
            if (candidates.Count == 1)
            {
                davItem = candidates[0];
                Log.Information("Found item by filename match: {Path}", davItem.Path);
            }
            else if (candidates.Count > 1)
            {
                throw new InvalidOperationException($"Multiple items found with filename '{fileName}'. Cannot determine target.");
            }
        }

        if (davItem == null) throw new FileNotFoundException($"Item not found: {filePath}");

        // when usenet article is missing, perform repairs
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Normalize AffinityKey from parent directory (matches WebDav file patterns)
        var rawAffinityKey = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
        var normalizedAffinityKey = FilenameNormalizer.NormalizeName(rawAffinityKey);
        using var _ = cts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Repair, new ConnectionUsageDetails { Text = davItem.Path, JobName = davItem.Name, AffinityKey = normalizedAffinityKey, DavItemId = davItem.Id }));
        await Repair(davItem, dbClient, cts.Token, "Manual repair triggered by user").ConfigureAwait(false);
    }

    private async Task Repair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct, string? failureDetails = null, string operation = "UNKNOWN")
    {
        try
        {
            var providerCount = _configManager.GetUsenetProviderConfig().Providers.Count;
            var failureReason = $"File had missing articles - Checked all {providerCount} providers" + (failureDetails != null ? $" ({failureDetails})" : "") + ".";

            // if the file extension has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blacklistedExtensions = _configManager.GetBlacklistedExtensions();
            if (blacklistedExtensions.Contains(Path.GetExtension(davItem.Name).ToLower()))
            {
                dbClient.Ctx.Items.Remove(davItem);
                OrganizedLinksUtil.RemoveCacheEntry(davItem.Id);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        failureReason,
                        "File extension is marked in settings as ignored (unwanted) file type.",
                        "Deleted file."
                    ]),
                    Operation = operation
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                await _providerErrorService.ClearErrorsForFile(davItem.Path).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is unlinked/orphaned,
            // then we can simply delete it.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null)
            {
                dbClient.Ctx.Items.Remove(davItem);
                OrganizedLinksUtil.RemoveCacheEntry(davItem.Id);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        failureReason,
                        "Could not find corresponding symlink or strm-file within Library Dir.",
                        "Deleted file."
                    ]),
                    Operation = operation
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                await _providerErrorService.ClearErrorsForFile(davItem.Path).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";

            foreach (var arrClient in _configManager.GetArrConfig().GetArrClients())
            {
                var rootFolders = await arrClient.GetRootFolders().ConfigureAwait(false);
                if (!rootFolders.Any(x => symlinkOrStrmPath.StartsWith(x.Path!)))
                {
                    Log.Debug($"[HealthCheck] Path '{symlinkOrStrmPath}' does not start with any root folder of Arr instance '{arrClient.Host}' (Roots: {string.Join(", ", rootFolders.Select(x => x.Path))}). Attempting search anyway to handle potential Docker path mappings.");
                }

                // Safety Check: Ensure the file points to our mount
                var mountDir = _configManager.GetRcloneMountDir();
                var linkInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(symlinkOrStrmPath));
                if (linkInfo is SymlinkAndStrmUtil.SymlinkInfo symInfo && !symInfo.TargetPath.StartsWith(mountDir))
                {
                    Log.Warning($"[HealthCheck] Safety check failed: Symlink {symlinkOrStrmPath} points to {symInfo.TargetPath}, which is outside mount dir {mountDir}. Skipping Arr delete.");
                    continue;
                }

                // Capture link info before deletion for logging
                var arrLinkInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(symlinkOrStrmPath));
                string linkTargetMsg = "";
                if (arrLinkInfo is SymlinkAndStrmUtil.SymlinkInfo sInfo)
                    linkTargetMsg = $" (Symlink target: '{sInfo.TargetPath}')";
                else if (arrLinkInfo is SymlinkAndStrmUtil.StrmInfo stInfo)
                    linkTargetMsg = $" (Strm URL: '{stInfo.TargetUrl}')";

                // If this is a Sonarr client, try to get the episode ID for more specific history lookup
                // episodeId is Sonarr-specific and not applicable to Radarr
                int? episodeId = null;
                if (arrClient is SonarrClient sonarrClient)
                {
                    try
                    {
                        var mediaIds = await sonarrClient.GetMediaIds(davItem.Path);
                        if (mediaIds != null && mediaIds.Value.episodeIds.Any())
                        {
                            episodeId = mediaIds.Value.episodeIds.First(); // Use the first episode ID found
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[HealthCheck] Failed to get episode ID from Sonarr '{arrClient.Host}': {ex.Message}");
                    }
                }

                try
                {
                    // Pass episodeId (Sonarr only) and sort parameters
                    if (await arrClient.RemoveAndSearch(symlinkOrStrmPath, episodeId: episodeId, sortKey: "date", sortDirection: "descending").ConfigureAwait(false))
                    {
                        var arrActionMessage = $"Successfully triggered Arr to remove file '{symlinkOrStrmPath}'{linkTargetMsg} and search for replacement.";
                        Log.Information($"[HealthCheck] {arrActionMessage}");

                        dbClient.Ctx.Items.Remove(davItem);
                        OrganizedLinksUtil.RemoveCacheEntry(davItem.Id);
                        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                        {
                            Id = Guid.NewGuid(),
                            DavItemId = davItem.Id,
                            Path = davItem.Path,
                            CreatedAt = DateTimeOffset.UtcNow,
                            Result = HealthCheckResult.HealthResult.Unhealthy,
                            RepairStatus = HealthCheckResult.RepairAction.Repaired,
                            Message = string.Join(" ", [
                                failureReason,
                                $"Corresponding {linkType} found within Library Dir.",
                                arrActionMessage
                            ]),
                            Operation = operation
                        }));
                        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                        await _providerErrorService.ClearErrorsForFile(davItem.Path).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[HealthCheck] Error during RemoveAndSearch on Arr instance '{arrClient.Host}': {ex.Message}");
                }

                // If RemoveAndSearch returned false or threw, it means this client didn't recognize or couldn't handle the file.
                // Log and continue to the next client (e.g. might have checked Radarr for a TV show).
                Log.Debug($"[HealthCheck] Arr instance '{arrClient.Host}' could not find/remove '{symlinkOrStrmPath}'. Checking next instance...");
                continue;
            }

            // if we could not find a corresponding arr instance
            // then we can delete both the item and the link-file.
            string deleteMessage;
            var fileInfoToDelete = new FileInfo(symlinkOrStrmPath);
            var linkInfoToDelete = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfoToDelete);
            
            if (linkInfoToDelete is SymlinkAndStrmUtil.SymlinkInfo symInfoToDelete)
            {
                deleteMessage = $"Deleting symlink '{symlinkOrStrmPath}' (target: '{symInfoToDelete.TargetPath}') and associated NzbDav item '{davItem.Path}'.";
            }
            else if (linkInfoToDelete is SymlinkAndStrmUtil.StrmInfo strmInfoToDelete)
            {
                deleteMessage = $"Deleting strm file '{symlinkOrStrmPath}' (target URL: '{strmInfoToDelete.TargetUrl}') and associated NzbDav item '{davItem.Path}'.";
            }
            else
            {
                deleteMessage = $"Deleting file '{symlinkOrStrmPath}' and associated NzbDav item '{davItem.Path}'.";
            }

            Log.Warning($"[HealthCheck] Could not find corresponding Radarr/Sonarr media-item for file: {davItem.Name}. {deleteMessage}");
            await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);
            dbClient.Ctx.Items.Remove(davItem);
            OrganizedLinksUtil.RemoveCacheEntry(davItem.Id);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.Deleted,
                Message = string.Join(" ", [
                    failureReason,
                    $"Corresponding {linkType} found within Library Dir.",
                    "Could not find corresponding Radarr/Sonarr media-item to trigger a new search.",
                    deleteMessage // Use the detailed delete message
                ]),
                Operation = operation
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await _providerErrorService.ClearErrorsForFile(davItem.Path).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            Log.Warning($"[HealthCheck] Repair failed for item {davItem.Name}: {e.Message}");

            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow.AddDays(1);
            davItem.IsCorrupted = true;
            davItem.CorruptionReason = $"Repair failed: {e.Message}";
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}",
                Operation = operation
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow.AddDays(1);
            davItem.IsCorrupted = true;
            davItem.CorruptionReason = $"Repair failed: {e.Message}";
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}",
                Operation = operation
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    public void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        foreach (var segmentId in segmentIds)
            if (_missingSegmentIds.ContainsKey(segmentId))
                throw new UsenetArticleNotFoundException(segmentId);
    }
}