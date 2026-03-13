using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class HistoryCleanupService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                var cleanupItem = await dbContext.HistoryCleanupItems
                    .FirstOrDefaultAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (cleanupItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Collect paths before bulk operation (bypasses EF change tracking)
                var affectedPaths = await dbContext.Items
                    .Where(x => x.HistoryItemId == cleanupItem.Id)
                    .Select(x => x.Path)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (cleanupItem.DeleteMountedFiles)
                {
                    var deleted = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteDeleteAsync(stoppingToken)
                        .ConfigureAwait(false);

                    Log.Information("[HistoryCleanup] Deleted {Count} DavItems for history item {Id}",
                        deleted, cleanupItem.Id);
                }
                else
                {
                    var updated = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteUpdateAsync(
                            x => x.SetProperty(p => p.HistoryItemId, (Guid?)null),
                            stoppingToken
                        ).ConfigureAwait(false);

                    Log.Debug("[HistoryCleanup] Unlinked {Count} DavItems from history item {Id}",
                        updated, cleanupItem.Id);
                }

                // Trigger vfs/forget for affected directories
                var dirsToForget = affectedPaths
                    .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToArray();
                DavDatabaseContext.TriggerVfsForget(dirsToForget!);

                dbContext.HistoryCleanupItems.Remove(cleanupItem);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, "[HistoryCleanup] Error processing cleanup queue: {Message}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
