using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// - This class takes care of monitoring Radarr/Sonarr instances
///   for stuck queue items which usually require manual intervention.
/// - NzbDAV can be configured to automatically remove these stuck items,
///   optionally block these stuck items, and optionally trigger a new
///   search for these stuck items.
/// - Also monitors for imported items to clean them up from history.
/// </summary>
public class ArrMonitoringService
{
    private readonly ConfigManager _configManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WebsocketManager _websocketManager;
    private readonly CancellationToken _cancellationToken = SigtermUtil.GetCancellationToken();
    private DateTime _lastImportCheck = DateTime.MinValue;

    public ArrMonitoringService(
        ConfigManager configManager,
        IServiceScopeFactory scopeFactory,
        WebsocketManager websocketManager)
    {
        _configManager = configManager;
        _scopeFactory = scopeFactory;
        _websocketManager = websocketManager;
        _ = StartMonitoringService();
    }

    private async Task StartMonitoringService()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            // Ensure delay runs on each iteration
            await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken).ConfigureAwait(false);

            var arrConfig = _configManager.GetArrConfig();
            
            // 1. Handle stuck queue items
            if (arrConfig.QueueRules.Any(x => x.Action != ArrConfig.QueueAction.DoNothing))
            {
                foreach (var arrClient in arrConfig.GetArrClients())
                    await HandleStuckQueueItems(arrConfig, arrClient).ConfigureAwait(false);
            }

            // 2. Cleanup imported items (every 60 seconds)
            if (DateTime.UtcNow - _lastImportCheck > TimeSpan.FromSeconds(60))
            {
                _lastImportCheck = DateTime.UtcNow;
                foreach (var arrClient in arrConfig.GetArrClients())
                    await CleanupImportedItems(arrClient).ConfigureAwait(false);

                // Also cleanup old failed items (1 hour retention)
                await CleanupOldFailedItems().ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupImportedItems(ArrClient client)
    {
        try
        {
            // Fetch recent imports (last 100)
            var history = await client.GetRecentImportsAsync(100).ConfigureAwait(false);
            
            // Extract NzbDav IDs (guids) and map them to their import date
            var importedIdsWithDates = history.Records
                .Select(r =>
                {
                    string? id = null;
                    if (r.Data.TryGetValue("guid", out var guid)) id = guid;
                    else if (r.Data.TryGetValue("downloadId", out var dlId)) id = dlId;
                    
                    if (string.IsNullOrEmpty(id) || !Guid.TryParse(id, out var g)) return null;
                    return new { Id = g, Date = r.Date };
                })
                .Where(x => x != null)
                .GroupBy(x => x!.Id)
                .ToDictionary(g => g.Key, g => g.Max(x => x!.Date));

            if (importedIdsWithDates.Count == 0) return;

            using var scope = _scopeFactory.CreateScope();
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();

            // Only delete items that exist in our history AND have been imported for more than X hours
            var retentionHours = _configManager.GetHistoryRetentionHours();
            var cutoffTime = DateTime.Now.AddHours(-retentionHours);

            // Get imported items from our DB
            var importedIds = importedIdsWithDates.Keys.ToList();
            var historyItems = await dbClient.Ctx.HistoryItems
                .Where(h => importedIds.Contains(h.Id))
                .Select(h => new { h.Id, h.JobName, h.CompletedAt })
                .ToListAsync(_cancellationToken).ConfigureAwait(false);

            // Filter items that were imported before the cutoff time
            var itemsToCleanup = historyItems
                .Where(h => importedIdsWithDates.TryGetValue(h.Id, out var importDate) && importDate < cutoffTime)
                .ToList();

            if (itemsToCleanup.Count == 0) return;

            Log.Information("[ArrMonitoring] Found {Count} imported items to cleanup for {Host}. Retention: {RetentionHours}h, CutoffTime: {CutoffTime}",
                itemsToCleanup.Count, client.Host, retentionHours, cutoffTime);

            foreach (var item in itemsToCleanup)
            {
                var importDate = importedIdsWithDates[item.Id];
                var ageSinceImport = DateTime.Now - importDate;
                Log.Information("[ArrMonitoring] Archiving imported item {JobName} ({Id}) - imported {Minutes:F1} minutes ago (at {ImportDate})",
                    item.JobName, item.Id, ageSinceImport.TotalMinutes, importDate);
            }

            var idsToArchive = itemsToCleanup.Select(x => x.Id).ToList();
            await dbClient.ArchiveHistoryItemsAsync(idsToArchive, _cancellationToken).ConfigureAwait(false);
            await dbClient.SaveChanges(_cancellationToken).ConfigureAwait(false);

            _ = _websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", idsToArchive));
        }
        catch (Exception e)
        {
            // Log.Debug because this might fail if Arr is down or API is different, don't spam errors
            Log.Debug($"Error cleaning up imported items for `{client.Host}`: {e.Message}");
        }
    }

    private async Task CleanupOldFailedItems()
    {
        try
        {
            var retentionHours = _configManager.GetHistoryRetentionHours();
            var cutoffTime = DateTime.Now.AddHours(-retentionHours);

            using var scope = _scopeFactory.CreateScope();
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();

            var totalHistoryItems = await dbClient.Ctx.HistoryItems.CountAsync(_cancellationToken).ConfigureAwait(false);

            // 1. Delete archived items that have been archived for > retention period
            var archivedItemsToDelete = await dbClient.Ctx.HistoryItems
                .Where(h => h.IsArchived && h.ArchivedAt.HasValue)
                .Where(h => h.ArchivedAt.Value < cutoffTime)
                .Select(h => new { h.Id, h.JobName, h.ArchivedAt })
                .ToListAsync(_cancellationToken).ConfigureAwait(false);

            if (archivedItemsToDelete.Count > 0)
            {
                Log.Information("[ArrMonitoring] Found {Count} archived items to delete. Archived before: {CutoffTime} (Retention: {RetentionHours}h)",
                    archivedItemsToDelete.Count, cutoffTime, retentionHours);

                foreach (var item in archivedItemsToDelete)
                {
                    var age = DateTime.Now - item.ArchivedAt!.Value;
                    Log.Information("[ArrMonitoring] Removing archived item {JobName} ({Id}) - archived {Minutes:F1} minutes ago",
                        item.JobName, item.Id, age.TotalMinutes);
                }

                var archivedIds = archivedItemsToDelete.Select(x => x.Id).ToList();
                await dbClient.RemoveHistoryItemsAsync(archivedIds, false, _cancellationToken).ConfigureAwait(false);
                await dbClient.SaveChanges(_cancellationToken).ConfigureAwait(false);
                _ = _websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", archivedIds));
            }

            // 2. Delete old failed items that are not archived (direct failures, not from Arr)
            var failedItemsToDelete = await dbClient.Ctx.HistoryItems
                .Where(h => h.DownloadStatus == Database.Models.HistoryItem.DownloadStatusOption.Failed)
                .Where(h => !h.IsArchived) // Only non-archived failed items
                .Where(h => h.CompletedAt < cutoffTime)
                .Select(h => new { h.Id, h.JobName, h.CompletedAt })
                .ToListAsync(_cancellationToken).ConfigureAwait(false);

            if (failedItemsToDelete.Count > 0)
            {
                Log.Information("[ArrMonitoring] Found {Count} old failed items to delete. Completed before: {CutoffTime} (Retention: {RetentionHours}h)",
                    failedItemsToDelete.Count, cutoffTime, retentionHours);

                foreach (var item in failedItemsToDelete)
                {
                    var age = DateTime.Now - item.CompletedAt;
                    Log.Information("[ArrMonitoring] Removing failed item {JobName} ({Id}) - completed {Minutes:F1} minutes ago",
                        item.JobName, item.Id, age.TotalMinutes);
                }

                var failedIds = failedItemsToDelete.Select(x => x.Id).ToList();
                await dbClient.RemoveHistoryItemsAsync(failedIds, true, _cancellationToken).ConfigureAwait(false);
                await dbClient.SaveChanges(_cancellationToken).ConfigureAwait(false);
                _ = _websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", failedIds));
            }

            if (archivedItemsToDelete.Count == 0 && failedItemsToDelete.Count == 0)
            {
                Log.Debug("[ArrMonitoring] No old items to cleanup. Total history items: {Total}", totalHistoryItems);
            }
        }
        catch (Exception e)
        {
            Log.Debug($"Error cleaning up old items: {e.Message}");
        }
    }

    private async Task HandleStuckQueueItems(ArrConfig arrConfig, ArrClient client)
    {
        try
        {
            var queueStatus = await client.GetQueueStatusAsync().ConfigureAwait(false);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync().ConfigureAwait(false);
            var actionableStatuses = arrConfig.QueueRules.Select(x => x.Message);
            var stuckRecords = queue.Records.Where(x => actionableStatuses.Any(x.HasStatusMessage));
            foreach (var record in stuckRecords)
                await HandleStuckQueueItem(record, arrConfig, client).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error($"Error occured while monitoring queue for `{client.Host}`: {e.Message}");
        }
    }

    private async Task HandleStuckQueueItem(ArrQueueRecord item, ArrConfig arrConfig, ArrClient client)
    {
        // since there may be multiple status messages, multiple actions may apply.
        // in such case, always perform the strongest action.
        var action = arrConfig.QueueRules
            .Where(x => item.HasStatusMessage(x.Message))
            .Select(x => x.Action)
            .DefaultIfEmpty(ArrConfig.QueueAction.DoNothing)
            .Max();

        if (action is ArrConfig.QueueAction.DoNothing) return;
        await client.DeleteQueueRecord(item.Id, action).ConfigureAwait(false);
        Log.Warning($"Resolved stuck queue item `{item.Title}` from `{client.Host}, with action `{action}`");
    }
}