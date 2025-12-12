using System.Collections.Concurrent;
using System.Net.Http;
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
    private readonly CancellationToken _cancellationToken = SigtermUtil.GetCancellationToken();

    private readonly HashSet<string> _missingSegmentIds = [];
    private readonly ConcurrentDictionary<Guid, int> _timeoutCounts = new();

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // when usenet host changes, clear the missing segments cache
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.host")) return;
            lock (_missingSegmentIds) _missingSegmentIds.Clear();
        };

        _ = StartMonitoringService();
    }

    private async Task StartMonitoringService()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            DavItem? davItem = null;
            try
            {
                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // get concurrency (not used anymore - operation limit enforces this)
                var concurrency = _configManager.GetMaxRepairConnections();

                // set connection usage context (no reservation needed - operation limits handle it)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(20)); // Timeout after 20 minutes per file to prevent blocking
                
                using var _1 = cts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.HealthCheck, new ConnectionUsageDetails { Text = "Health Check" }));

                // get the davItem to health-check
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);
                var currentDateTime = DateTimeOffset.UtcNow;
                davItem = await GetHealthCheckQueueItems(dbClient)
                    .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime)
                    .FirstOrDefaultAsync(cts.Token).ConfigureAwait(false);

                // if there is no item to health-check, don't do anything
                if (davItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                    continue;
                }

                // perform the health check
                Log.Information($"[HealthCheck] Processing item: {davItem.Name} ({davItem.Id})");
                await PerformHealthCheck(davItem, dbClient, concurrency, cts.Token).ConfigureAwait(false);
                
                // Success! Remove from timeout tracking
                _timeoutCounts.TryRemove(davItem.Id, out _);
                
                Log.Information($"[HealthCheck] Finished item: {davItem.Name}");
            }
            catch (OperationCanceledException) when (!_cancellationToken.IsCancellationRequested)
            {
                if (davItem != null)
                {
                    var timeouts = _timeoutCounts.AddOrUpdate(davItem.Id, 1, (_, count) => count + 1);

                    if (timeouts >= 2)
                    {
                        Log.Error($"[HealthCheck] Item {davItem.Name} timed out {timeouts} times. Marking as failed.");
                        _timeoutCounts.TryRemove(davItem.Id, out _);

                        try
                        {
                            await using var dbContext = new DavDatabaseContext();
                            dbContext.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                            {
                                Id = Guid.NewGuid(),
                                DavItemId = davItem.Id,
                                Path = davItem.Path,
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
                        Log.Warning($"[HealthCheck] Timed out processing item: {davItem.Name}. Rescheduling for later (Attempt {timeouts}).");
                        try
                        {
                            using var dbContext = new DavDatabaseContext();
                            await dbContext.Items
                                .Where(x => x.Id == davItem.Id)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.NextHealthCheck, DateTimeOffset.UtcNow.AddHours(1)))
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[HealthCheck] Failed to reschedule timed-out item.");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error performing background health checks: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken).ConfigureAwait(false);
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
        var actionNeeded = HealthCheckResult.RepairAction.ActionNeeded;
        var healthCheckResults = dbClient.Ctx.HealthCheckResults;
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.NzbFile
                        || x.Type == DavItem.ItemType.RarFile
                        || x.Type == DavItem.ItemType.MultipartFile)
            .Where(x => !healthCheckResults.Any(h => h.DavItemId == x.Id && h.RepairStatus == actionNeeded));
    }

    private async Task PerformHealthCheck
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct
    )
    {
        List<string> segments = [];
        try
        {
            // update the release date, if null
            segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            Log.Debug($"[HealthCheck] Fetched {segments.Count} segments for {davItem.Name}");
            if (davItem.ReleaseDate == null) await UpdateReleaseDate(davItem, segments, ct).ConfigureAwait(false);


            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
            progressHook.ProgressChanged += (_, progress) =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // perform health check
            Log.Debug($"[HealthCheck] Verifying segments for {davItem.Name}...");
            var progress = progressHook.ToPercentage(segments.Count);
            using var healthCheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var contextScope = healthCheckCts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.HealthCheck, new ConnectionUsageDetails { Text = davItem.Path }));
            await _usenetClient.CheckAllSegmentsAsync(segments, concurrency, progress, healthCheckCts.Token).ConfigureAwait(false);
            Log.Debug($"[HealthCheck] Segments verified for {davItem.Name}. Updating database...");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            // update the database
            davItem.LastHealthCheck = DateTimeOffset.UtcNow;
            davItem.NextHealthCheck = davItem.ReleaseDate + 2 * (davItem.LastHealthCheck - davItem.ReleaseDate);
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

            Log.Warning($"[HealthCheck] Health check failed for item {davItem.Name} (Missing Segment: {e.SegmentId}). {failureDetails}. Attempting repair.");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            if (FilenameUtil.IsImportantFileType(davItem.Name))
                lock (_missingSegmentIds)
                    _missingSegmentIds.Add(e.SegmentId);

            // when usenet article is missing, perform repairs
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var _3 = cts2.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Repair, new ConnectionUsageDetails { Text = davItem.Path }));
            await Repair(davItem, dbClient, cts2.Token, failureDetails).ConfigureAwait(false);
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
                .Where(x => x.Id == davItem.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        if (davItem.Type == DavItem.ItemType.MultipartFile)
        {
            var multipartFile = await dbClient.Ctx.MultipartFiles
                .Where(x => x.Id == davItem.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        return [];
    }

    public async Task TriggerManualRepairAsync(string filePath, DavDatabaseClient dbClient, CancellationToken ct)
    {
        Log.Information($"Manual repair triggered for file: {filePath}");
        
        // 1. Try exact match
        var davItem = await dbClient.Ctx.Items.FirstOrDefaultAsync(x => x.Path == filePath, ct).ConfigureAwait(false);
        
        // 2. Try unescaped match
        if (davItem == null)
        {
            var unescapedPath = Uri.UnescapeDataString(filePath);
            if (unescapedPath != filePath)
            {
                davItem = await dbClient.Ctx.Items.FirstOrDefaultAsync(x => x.Path == unescapedPath, ct).ConfigureAwait(false);
            }
        }

        // 3. Try match by filename (if unique)
        if (davItem == null)
        {
            var fileName = Path.GetFileName(filePath);
            var candidates = await dbClient.Ctx.Items.Where(x => x.Name == fileName).ToListAsync(ct).ConfigureAwait(false);
            if (candidates.Count == 1)
            {
                davItem = candidates[0];
                Log.Information($"Found item by filename match: {davItem.Path}");
            }
            else if (candidates.Count > 1)
            {
                throw new InvalidOperationException($"Multiple items found with filename '{fileName}'. Cannot determine target.");
            }
        }

        if (davItem == null) throw new FileNotFoundException($"Item not found: {filePath}");

        // when usenet article is missing, perform repairs
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var _ = cts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Repair, new ConnectionUsageDetails { Text = davItem.Path }));
        await Repair(davItem, dbClient, cts.Token, "Manual repair triggered by user").ConfigureAwait(false);
    }

    private async Task Repair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct, string? failureDetails = null)
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
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is unlinked/orphaned,
            // then we can simply delete it.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null)
            {
                dbClient.Ctx.Items.Remove(davItem);
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
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";
            foreach (var arrClient in _configManager.GetArrConfig().GetArrClients())
            {
                var rootFolders = await arrClient.GetRootFolders().ConfigureAwait(false);
                if (!rootFolders.Any(x => symlinkOrStrmPath.StartsWith(x.Path!))) continue;

                // if we found a corresponding arr instance,
                // then remove and search.
                if (await arrClient.RemoveAndSearch(symlinkOrStrmPath).ConfigureAwait(false))
                {
                    dbClient.Ctx.Items.Remove(davItem);
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
                            "Triggered new Arr search."
                        ])
                    }));
                    await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    return;
                }

                // if we could not find corresponding media-item to remove-and-search
                // within the found arr instance, then break out of this loop so that
                // we can fall back to the behavior below of deleting both the link-file
                // and the dav-item.
                break;
            }

            // if we could not find a corresponding arr instance
            // then we can delete both the item and the link-file.
            Log.Warning($"[HealthCheck] Could not find corresponding Radarr/Sonarr media-item for file: {davItem.Name}. Deleting file and link.");
            await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);
            dbClient.Ctx.Items.Remove(davItem);
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
                    $"Deleted the webdav-file and {linkType}."
                ])
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            Log.Warning($"[HealthCheck] Repair failed for item {davItem.Name}: {e.Message}");

            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = null;
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}"
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = null;
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}"
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
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds)
                if (_missingSegmentIds.Contains(segmentId))
                    throw new UsenetArticleNotFoundException(segmentId);
        }
    }
}