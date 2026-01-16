using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that periodically calculates and caches provider usage statistics
/// </summary>
public class ProviderStatsService
{
    private readonly CancellationToken _cancellationToken = SigtermUtil.GetCancellationToken();
    private readonly Dictionary<int, ProviderStatsResponse> _cachedStats = new();
    private readonly object _lock = new();

    // Time windows to pre-calculate (in hours)
    private readonly int[] _timeWindows = { 24, 72, 168, 336, 720 }; // 24h, 3d, 7d, 14d, 30d

    public ProviderStatsService()
    {
        _ = StartStatsCalculationLoop();
    }

    /// <summary>
    /// Gets the cached provider statistics for a specific time window
    /// </summary>
    public ProviderStatsResponse? GetCachedStats(int hours)
    {
        lock (_lock)
        {
            return _cachedStats.GetValueOrDefault(hours);
        }
    }

    private async Task StartStatsCalculationLoop()
    {
        // Calculate all time windows immediately on startup
        foreach (var hours in _timeWindows)
        {
            await CalculateAndCacheStats(hours).ConfigureAwait(false);
        }

        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait 15 minutes before next calculation
                await Task.Delay(TimeSpan.FromMinutes(15), _cancellationToken).ConfigureAwait(false);

                // Recalculate all time windows
                foreach (var hours in _timeWindows)
                {
                    await CalculateAndCacheStats(hours).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!_cancellationToken.IsCancellationRequested)
            {
                Log.Error(ex, "Error in provider stats calculation loop");
                await Task.Delay(TimeSpan.FromMinutes(1), _cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CalculateAndCacheStats(int hours)
    {
        try
        {
            var timeWindow = TimeSpan.FromHours(hours);
            var startTime = DateTimeOffset.UtcNow.Add(-timeWindow);

            await using var dbContext = new DavDatabaseContext();

            // Query events from the specified time window with operation type
            var events = await dbContext.ProviderUsageEvents
                .Where(e => e.CreatedAt >= startTime && e.OperationType != null)
                .Select(e => new { e.ProviderHost, e.ProviderType, e.OperationType })
                .ToListAsync(_cancellationToken)
                .ConfigureAwait(false);

            if (events.Count == 0)
            {
                lock (_lock)
                {
                    _cachedStats[hours] = new ProviderStatsResponse
                    {
                        Providers = new List<ProviderStats>(),
                        TotalOperations = 0,
                        CalculatedAt = DateTimeOffset.UtcNow,
                        TimeWindow = timeWindow,
                        TimeWindowHours = hours
                    };
                }
                return;
            }

            // Group by provider and calculate stats
            var providerGroups = events
                .GroupBy(e => new { e.ProviderHost, e.ProviderType })
                .Select(g => new
                {
                    g.Key.ProviderHost,
                    g.Key.ProviderType,
                    TotalOperations = (long)g.Count(),
                    OperationCounts = g.GroupBy(e => e.OperationType!)
                        .ToDictionary(og => og.Key, og => (long)og.Count())
                })
                .ToList();

            var totalOperations = (long)events.Count;

            var providerStats = providerGroups
                .Select(pg => new ProviderStats
                {
                    ProviderHost = pg.ProviderHost,
                    ProviderType = pg.ProviderType,
                    TotalOperations = pg.TotalOperations,
                    OperationCounts = pg.OperationCounts,
                    PercentageOfTotal = totalOperations > 0
                        ? Math.Round((double)pg.TotalOperations / totalOperations * 100, 1)
                        : 0
                })
                .OrderByDescending(ps => ps.TotalOperations)
                .ToList();

            lock (_lock)
            {
                _cachedStats[hours] = new ProviderStatsResponse
                {
                    Providers = providerStats,
                    TotalOperations = totalOperations,
                    CalculatedAt = DateTimeOffset.UtcNow,
                    TimeWindow = timeWindow,
                    TimeWindowHours = hours
                };
            }

            Log.Debug("Provider stats calculated for {Hours}h window: {TotalOperations} operations across {ProviderCount} providers",
                hours, totalOperations, providerStats.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to calculate provider stats");
        }
    }
}
