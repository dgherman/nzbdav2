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
        // Delay initial calculation to avoid competing with other startup tasks
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Calculate all time windows
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

            // Query 1: Get total operations per provider (database-side GROUP BY)
            var providerTotals = await dbContext.ProviderUsageEvents
                .Where(e => e.CreatedAt >= startTime && e.OperationType != null)
                .GroupBy(e => new { e.ProviderHost, e.ProviderType })
                .Select(g => new
                {
                    g.Key.ProviderHost,
                    g.Key.ProviderType,
                    TotalOperations = (long)g.Count()
                })
                .ToListAsync(_cancellationToken)
                .ConfigureAwait(false);

            if (providerTotals.Count == 0)
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

            // Query 2: Get per-operation breakdown (database-side GROUP BY)
            var operationBreakdown = await dbContext.ProviderUsageEvents
                .Where(e => e.CreatedAt >= startTime && e.OperationType != null)
                .GroupBy(e => new { e.ProviderHost, e.ProviderType, e.OperationType })
                .Select(g => new
                {
                    g.Key.ProviderHost,
                    g.Key.ProviderType,
                    OperationType = g.Key.OperationType!,
                    Count = (long)g.Count()
                })
                .ToListAsync(_cancellationToken)
                .ConfigureAwait(false);

            // Build operation counts dictionary per provider (from ~20-50 rows, not 3M)
            var operationCountsByProvider = operationBreakdown
                .GroupBy(x => new { x.ProviderHost, x.ProviderType })
                .ToDictionary(
                    g => (g.Key.ProviderHost, g.Key.ProviderType),
                    g => g.ToDictionary(x => x.OperationType, x => x.Count)
                );

            var totalOperations = providerTotals.Sum(p => p.TotalOperations);

            var providerStats = providerTotals
                .Select(pt => new ProviderStats
                {
                    ProviderHost = pt.ProviderHost,
                    ProviderType = pt.ProviderType,
                    TotalOperations = pt.TotalOperations,
                    OperationCounts = operationCountsByProvider
                        .GetValueOrDefault((pt.ProviderHost, pt.ProviderType),
                            new Dictionary<string, long>()),
                    PercentageOfTotal = totalOperations > 0
                        ? Math.Round((double)pt.TotalOperations / totalOperations * 100, 1)
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
