using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Tracks provider performance per NZB to optimize provider selection.
/// Records success rates and download speeds to prefer fast, reliable providers for each NZB.
/// Incorporates benchmark results with higher weight than per-segment speed.
/// </summary>
public class NzbProviderAffinityService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ProviderPerformance>> _stats = new();
    private readonly ConcurrentDictionary<int, BenchmarkSpeed> _benchmarkSpeeds = new();
    private readonly Timer _persistenceTimer;
    private readonly Timer _benchmarkRefreshTimer;
    private readonly SemaphoreSlim _dbWriteLock = new(1, 1);

    public NzbProviderAffinityService(
        IServiceScopeFactory scopeFactory,
        ConfigManager configManager)
    {
        _scopeFactory = scopeFactory;
        _configManager = configManager;

        // Persist stats every 5 seconds
        _persistenceTimer = new Timer(PersistStats, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Refresh benchmark speeds every 60 seconds (in case new benchmarks are run)
        _benchmarkRefreshTimer = new Timer(_ => _ = Task.Run(LoadBenchmarkSpeedsAsync), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));

        // Load existing stats and benchmark data from database
        _ = Task.Run(async () =>
        {
            await LoadStatsAsync().ConfigureAwait(false);
            await LoadBenchmarkSpeedsAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Force reload of benchmark speeds from database (called after new benchmark completes)
    /// </summary>
    public Task RefreshBenchmarkSpeeds() => LoadBenchmarkSpeedsAsync();

    /// <summary>
    /// Record a successful segment download with timing information
    /// </summary>
    public void RecordSuccess(string jobName, int providerIndex, long bytes, long elapsedMs)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;
        if (string.IsNullOrEmpty(jobName)) return;

        var jobStats = _stats.GetOrAdd(jobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
        var providerStats = jobStats.GetOrAdd(providerIndex, _ => new ProviderPerformance());

        providerStats.RecordSuccess(bytes, elapsedMs);
    }

    /// <summary>
    /// Record a failed segment download
    /// </summary>
    public void RecordFailure(string jobName, int providerIndex)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;
        if (string.IsNullOrEmpty(jobName)) return;

        var jobStats = _stats.GetOrAdd(jobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
        var providerStats = jobStats.GetOrAdd(providerIndex, _ => new ProviderPerformance());

        providerStats.RecordFailure();

        // Log straggler failures to help diagnose slow provider issues
        Log.Debug("[NzbProviderAffinity] RecordFailure: Job={JobName}, Provider={ProviderIndex}, TotalFailures={Failures}",
            jobName, providerIndex, providerStats.FailedSegments);
    }

    /// <summary>
    /// Get the preferred provider index for an NZB based on performance history.
    /// Uses epsilon-greedy strategy: exploits best provider most of the time,
    /// but explores other providers 10% of the time to gather performance data.
    /// Incorporates benchmark speeds when available for better provider selection.
    /// Returns null if no preference exists or affinity is disabled.
    /// </summary>
    /// <param name="jobName">The normalized job/NZB name</param>
    /// <param name="totalProviders">Total number of providers (for exploration)</param>
    /// <param name="logDecision">Whether to log the decision details</param>
    /// <param name="usageType">The type of operation (streaming, queue, etc.) - affects whether to defer to non-fastest provider</param>
    public int? GetPreferredProvider(string jobName, int totalProviders = 0, bool logDecision = false, ConnectionUsageType usageType = ConnectionUsageType.Streaming)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return null;
        if (string.IsNullOrEmpty(jobName)) return null;
        if (!_stats.TryGetValue(jobName, out var jobStats)) return null;

        // Get provider configuration for type filtering
        var providerConfig = _configManager.GetUsenetProviderConfig();

        // Epsilon-greedy exploration strategy: 10% exploration, 90% exploitation
        const double explorationRate = 0.10;
        var shouldExplore = Random.Shared.NextDouble() < explorationRate;

        if (shouldExplore && totalProviders > 0)
        {
            // Exploration: Choose a provider that hasn't been tested enough
            // Only explore Pooled and BackupAndStats providers, exclude BackupOnly providers
            var explorableProviders = Enumerable.Range(0, totalProviders)
                .Where(providerIndex =>
                {
                    // Check if provider should participate in exploration
                    if (providerIndex >= providerConfig.Providers.Count)
                        return false; // Provider index out of range

                    var providerType = providerConfig.Providers[providerIndex].Type;
                    // Only explore Pooled and BackupAndStats providers
                    return providerType == Models.ProviderType.Pooled ||
                           providerType == Models.ProviderType.BackupAndStats;
                })
                .Where(providerIndex => !jobStats.ContainsKey(providerIndex) || jobStats[providerIndex].SuccessfulSegments < 10)
                .ToList();

            if (explorableProviders.Count > 0)
            {
                var exploredProvider = explorableProviders[Random.Shared.Next(explorableProviders.Count)];
                return exploredProvider;
            }
        }

        // Exploitation: Use the best known provider
        // Require at least 10 successful segments before establishing a preference
        // Only consider Pooled and BackupAndStats providers, exclude BackupOnly
        const int minSuccessfulSegments = 10;

        var eligibleProviders = jobStats
            .Where(kvp => kvp.Value.SuccessfulSegments >= minSuccessfulSegments)
            .Where(kvp =>
            {
                // Check if provider should be considered for affinity preference
                if (kvp.Key >= providerConfig.Providers.Count)
                    return false; // Provider index out of range

                var providerType = providerConfig.Providers[kvp.Key].Type;
                // Only consider Pooled and BackupAndStats providers
                return providerType == Models.ProviderType.Pooled ||
                       providerType == Models.ProviderType.BackupAndStats;
            })
            .ToList();

        if (eligibleProviders.Count == 0) return null;

        // Find the maximum speed among all providers for normalization
        var maxSpeed = eligibleProviders.Max(kvp => kvp.Value.AverageSpeedBps);
        if (maxSpeed == 0) maxSpeed = 1; // Avoid division by zero

        // Get maximum benchmark speed for normalization
        var maxBenchmarkSpeed = _benchmarkSpeeds.Count > 0
            ? _benchmarkSpeeds.Values.Max(b => b.SpeedMbps)
            : 0.0;

        // Determine weighting based on whether we have benchmark data
        // With benchmark data: Success rate 40%, Benchmark speed 35%, Segment speed 25%
        // Without benchmark data for provider: Success rate 45%, Segment speed 35%
        // No benchmark data at all: Success rate 45%, Segment speed 55%
        var hasBenchmarkData = _benchmarkSpeeds.Count > 0;

        var candidates = eligibleProviders
            .Select(kvp =>
            {
                var normalizedSuccessRate = kvp.Value.SuccessRate; // Already 0-100
                var normalizedSegmentSpeed = (kvp.Value.AverageSpeedBps / (double)maxSpeed) * 100.0;

                double score;
                if (hasBenchmarkData && _benchmarkSpeeds.TryGetValue(kvp.Key, out var benchmark))
                {
                    // We have benchmark data for this provider
                    var normalizedBenchmarkSpeed = maxBenchmarkSpeed > 0
                        ? (benchmark.SpeedMbps / maxBenchmarkSpeed) * 100.0
                        : 50.0; // Default to middle if no max

                    // Weighted scoring: success rate 40%, benchmark 35%, segment speed 25%
                    score = (normalizedSuccessRate * 0.40) +
                            (normalizedBenchmarkSpeed * 0.35) +
                            (normalizedSegmentSpeed * 0.25);
                }
                else if (hasBenchmarkData)
                {
                    // We have benchmark data for other providers but not this one
                    // Penalize slightly by using lower weight on segment speed
                    score = (normalizedSuccessRate * 0.45) + (normalizedSegmentSpeed * 0.35);
                }
                else
                {
                    // No benchmark data at all - use original weighting
                    score = (normalizedSuccessRate * 0.45) + (normalizedSegmentSpeed * 0.55);
                }

                return new
                {
                    ProviderIndex = kvp.Key,
                    Stats = kvp.Value,
                    NormalizedSuccessRate = normalizedSuccessRate,
                    NormalizedSpeed = normalizedSegmentSpeed,
                    Score = score
                };
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (candidates.Count == 0) return null;

        // Check if we should defer to a non-fastest provider for background operations
        var shouldDefer = ShouldDeferToStreaming(usageType) && candidates.Count > 1;
        var selectedCandidate = shouldDefer ? candidates[1] : candidates[0];

        if (logDecision)
        {
            Log.Debug("[NzbProviderAffinity] Selected provider {ProviderIndex} for job {JobName} with score {Score:F2} (deferred={Deferred})",
                selectedCandidate.ProviderIndex, jobName, selectedCandidate.Score, shouldDefer);
        }

        return selectedCandidate.ProviderIndex;
    }

    /// <summary>
    /// Determines if the current operation should defer to non-fastest provider
    /// to avoid competing with streaming operations.
    /// Priority order: BufferedStreaming/Streaming > HealthCheck/Queue/Analysis
    /// </summary>
    private static bool ShouldDeferToStreaming(ConnectionUsageType usageType)
    {
        // Background operations should defer to streaming when possible
        return usageType is ConnectionUsageType.Queue
            or ConnectionUsageType.HealthCheck
            or ConnectionUsageType.Analysis;
    }

    /// <summary>
    /// Get all provider statistics for a specific NZB
    /// </summary>
    public Dictionary<int, NzbProviderStats> GetJobStats(string jobName)
    {
        if (!_stats.TryGetValue(jobName, out var jobStats))
            return new Dictionary<int, NzbProviderStats>();

        var result = new Dictionary<int, NzbProviderStats>();
        foreach (var (providerIndex, performance) in jobStats)
        {
            var stats = new NzbProviderStats
            {
                JobName = jobName,
                ProviderIndex = providerIndex
            };
            performance.CopyTo(stats);
            result[providerIndex] = stats;
        }
        return result;
    }

    /// <summary>
    /// Clear statistics for a specific job (when queue item is deleted)
    /// </summary>
    public void ClearJobStats(string jobName)
    {
        _stats.TryRemove(jobName, out _);
    }

    /// <summary>
    /// Clear all in-memory statistics (does not affect database).
    /// Call this when stats are reset via the API to ensure consistency.
    /// </summary>
    public void ClearAllStats()
    {
        _stats.Clear();
        Log.Information("[NzbProviderAffinity] Cleared all in-memory provider stats");
    }

    /// <summary>
    /// Get providers with success rate below threshold for a specific job.
    /// Used for soft deprioritization - these providers are used as last resort.
    /// </summary>
    /// <param name="jobName">The job/NZB name to check</param>
    /// <param name="minSamples">Minimum number of operations before triggering (default: 10)</param>
    /// <param name="successRateThreshold">Success rate threshold in percent (default: 30%)</param>
    /// <returns>Set of provider indices with low success rates</returns>
    public HashSet<int> GetLowSuccessRateProviders(string jobName, int minSamples = 10, double successRateThreshold = 30.0)
    {
        var result = new HashSet<int>();
        if (!_configManager.IsProviderAffinityEnabled()) return result;
        if (string.IsNullOrEmpty(jobName)) return result;
        if (!_stats.TryGetValue(jobName, out var jobStats)) return result;

        foreach (var (providerIndex, performance) in jobStats)
        {
            var totalSegments = performance.SuccessfulSegments + performance.FailedSegments;
            if (totalSegments < minSamples) continue;

            if (performance.SuccessRate < successRateThreshold)
            {
                result.Add(providerIndex);
                Log.Debug("[NzbProviderAffinity] Provider {ProviderIndex} has low success rate {SuccessRate:F1}% for job {JobName} (threshold: {Threshold}%)",
                    providerIndex, performance.SuccessRate, jobName, successRateThreshold);
            }
        }
        return result;
    }

    private async Task LoadStatsAsync()
    {
        try
        {
            await _dbWriteLock.WaitAsync();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                var allStats = await dbContext.NzbProviderStats
                    .AsNoTracking()
                    .ToListAsync()
                    .ConfigureAwait(false);

                foreach (var stat in allStats)
                {
                    var jobStats = _stats.GetOrAdd(stat.JobName, _ => new ConcurrentDictionary<int, ProviderPerformance>());
                    var providerStats = jobStats.GetOrAdd(stat.ProviderIndex, _ => new ProviderPerformance());

                    providerStats.LoadFromDb(stat);
                }
            }
            finally
            {
                _dbWriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbProviderAffinity] Failed to load stats from database");
        }
    }

    private async Task LoadBenchmarkSpeedsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Get the most recent successful benchmark result for each provider
            var latestResults = await dbContext.ProviderBenchmarkResults
                .Where(r => r.Success && !r.IsLoadBalanced)
                .GroupBy(r => r.ProviderIndex)
                .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
                .AsNoTracking()
                .ToListAsync()
                .ConfigureAwait(false);

            _benchmarkSpeeds.Clear();
            foreach (var result in latestResults)
            {
                _benchmarkSpeeds[result.ProviderIndex] = new BenchmarkSpeed(
                    result.ProviderIndex,
                    result.SpeedMbps,
                    result.CreatedAt);
            }

            if (latestResults.Count > 0)
            {
                Log.Debug("[NzbProviderAffinity] Loaded benchmark speeds for {Count} providers", latestResults.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[NzbProviderAffinity] Failed to load benchmark speeds from database");
        }
    }

    private async void PersistStats(object? state)
    {
        if (!_configManager.IsProviderAffinityEnabled()) return;

        try
        {
            await _dbWriteLock.WaitAsync();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                var now = DateTimeOffset.UtcNow;
                var hasChanges = false;

                foreach (var (jobName, jobStats) in _stats)
                {
                    foreach (var (providerIndex, performance) in jobStats)
                    {
                        if (!performance.IsDirty) continue;

                        var dbStats = await dbContext.NzbProviderStats
                            .FindAsync(jobName, providerIndex)
                            .ConfigureAwait(false);

                        if (dbStats == null)
                        {
                            dbStats = new NzbProviderStats
                            {
                                JobName = jobName,
                                ProviderIndex = providerIndex
                            };
                            dbContext.NzbProviderStats.Add(dbStats);
                        }

                        performance.SaveToDb(dbStats, now);
                        hasChanges = true;
                    }
                }

                if (hasChanges)
                {
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _dbWriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbProviderAffinity] Failed to persist stats to database");
        }
    }

    private class ProviderPerformance
    {
        private readonly object _lock = new();
        private int _successfulSegments;
        private int _failedSegments;
        private long _totalBytes;
        private long _totalTimeMs;
        private long _recentAverageSpeedBps;
        private bool _isDirty;

        // EWMA smoothing factor: 0.15 means 15% weight to new data, 85% to existing average
        private const double Alpha = 0.15;
        // Outlier rejection: reject speeds outside of [0.1x, 10x] of current average
        private const double OutlierMinFactor = 0.1;
        private const double OutlierMaxFactor = 10.0;

        public int SuccessfulSegments => _successfulSegments;
        public int FailedSegments => _failedSegments;
        public long TotalBytes => _totalBytes;
        public long TotalTimeMs => _totalTimeMs;
        public long RecentAverageSpeedBps => _recentAverageSpeedBps;
        public bool IsDirty => _isDirty;

        public double SuccessRate
        {
            get
            {
                var total = _successfulSegments + _failedSegments;
                return total > 0 ? (_successfulSegments * 100.0) / total : 0;
            }
        }

        public long AverageSpeedBps
        {
            get
            {
                return _totalTimeMs > 0 ? (_totalBytes * 1000) / _totalTimeMs : 0;
            }
        }

        public void RecordSuccess(long bytes, long elapsedMs)
        {
            lock (_lock)
            {
                _successfulSegments++;
                _totalBytes += bytes;
                _totalTimeMs += elapsedMs;

                // Calculate current segment speed
                var currentSpeed = elapsedMs > 0 ? (bytes * 1000) / elapsedMs : 0;

                // Initialize EWMA on first segment or apply outlier rejection
                if (_recentAverageSpeedBps == 0)
                {
                    _recentAverageSpeedBps = currentSpeed;
                }
                else
                {
                    // Check if current speed is an outlier
                    var minSpeed = (long)(_recentAverageSpeedBps * OutlierMinFactor);
                    var maxSpeed = (long)(_recentAverageSpeedBps * OutlierMaxFactor);

                    if (currentSpeed >= minSpeed && currentSpeed <= maxSpeed)
                    {
                        // Apply EWMA: new_avg = alpha * current + (1-alpha) * old_avg
                        _recentAverageSpeedBps = (long)(Alpha * currentSpeed + (1 - Alpha) * _recentAverageSpeedBps);
                    }
                    // else: reject outlier, keep existing average
                }

                _isDirty = true;
            }
        }

        public void RecordFailure()
        {
            lock (_lock)
            {
                _failedSegments++;
                _isDirty = true;
            }
        }

        public void LoadFromDb(NzbProviderStats dbStats)
        {
            lock (_lock)
            {
                _successfulSegments = dbStats.SuccessfulSegments;
                _failedSegments = dbStats.FailedSegments;
                _totalBytes = dbStats.TotalBytes;
                _totalTimeMs = dbStats.TotalTimeMs;
                _recentAverageSpeedBps = dbStats.RecentAverageSpeedBps;
                _isDirty = false;
            }
        }

        public void CopyTo(NzbProviderStats dbStats)
        {
            lock (_lock)
            {
                dbStats.SuccessfulSegments = _successfulSegments;
                dbStats.FailedSegments = _failedSegments;
                dbStats.TotalBytes = _totalBytes;
                dbStats.TotalTimeMs = _totalTimeMs;
                dbStats.RecentAverageSpeedBps = _recentAverageSpeedBps;
                dbStats.LastUsed = DateTimeOffset.UtcNow;
            }
        }

        public void SaveToDb(NzbProviderStats dbStats, DateTimeOffset now)
        {
            lock (_lock)
            {
                dbStats.SuccessfulSegments = _successfulSegments;
                dbStats.FailedSegments = _failedSegments;
                dbStats.TotalBytes = _totalBytes;
                dbStats.TotalTimeMs = _totalTimeMs;
                dbStats.RecentAverageSpeedBps = _recentAverageSpeedBps;
                dbStats.LastUsed = now;
                _isDirty = false;
            }
        }
    }

    /// <summary>
    /// Cached benchmark speed data for a provider
    /// </summary>
    internal record BenchmarkSpeed(int ProviderIndex, double SpeedMbps, DateTimeOffset MeasuredAt);
}
