using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class DatabaseMaintenanceService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("[DatabaseMaintenance] Service started. Scheduled to run every 24 hours.");

        // Wait a bit on startup to let other heavy tasks finish
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DatabaseMaintenance] Error occurred during scheduled maintenance.");
            }

            // Run daily
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    public async Task PerformMaintenanceAsync(CancellationToken stoppingToken)
    {
        Log.Information("[DatabaseMaintenance] Starting daily database maintenance...");

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        // 1. Prune BandwidthSamples (> 30 days)
        var bandwidthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var bandwidthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"BandwidthSamples\" WHERE \"Timestamp\" < {bandwidthCutoff}", 
            stoppingToken);
        if (bandwidthDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from BandwidthSamples.", bandwidthDeleted);

        // 2. Prune HealthCheckResults (> 30 days)
        // Keep Deleted items longer? For now, treat all same as 30 days history.
        var healthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var healthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"HealthCheckResults\" WHERE \"CreatedAt\" < {healthCutoff}", 
            stoppingToken);
        if (healthDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from HealthCheckResults.", healthDeleted);

        // 3. Prune MissingArticleEvents (> 14 days)
        // These can grow huge, so aggressive pruning is good.
        var eventsCutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        var eventsDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"MissingArticleEvents\" WHERE \"Timestamp\" < {eventsCutoff}", 
            stoppingToken);
        if (eventsDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from MissingArticleEvents.", eventsDeleted);

        // 4. Prune MissingArticleSummaries (> 14 days last seen)
        var summaryCutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        var summariesDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"MissingArticleSummaries\" WHERE \"LastSeen\" < {summaryCutoff}",
            stoppingToken);
        if (summariesDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from MissingArticleSummaries.", summariesDeleted);

        // 5. Cleanup old hidden history items (> 30 days)
        var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        try
        {
            await dbClient.CleanupOldHiddenHistoryItemsAsync(30, stoppingToken);
            Log.Information("[DatabaseMaintenance] Cleaned up old hidden history items.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DatabaseMaintenance] Error cleaning up old hidden history items.");
        }

        // 6. Optimize WAL (Checkpoint)
        // This merges the WAL file into the main DB and truncates it, keeping disk usage low.
        Log.Information("[DatabaseMaintenance] Checkpointing WAL file...");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", stoppingToken);

        Log.Information("[DatabaseMaintenance] Maintenance completed successfully.");
    }
}
