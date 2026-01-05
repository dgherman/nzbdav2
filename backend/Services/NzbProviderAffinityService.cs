using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Tracks provider performance per NZB to optimize provider selection.
/// Records success rates and download speeds to prefer fast, reliable providers for each NZB.
/// </summary>
public class NzbProviderAffinityService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ProviderPerformance>> _stats = new();
    private readonly Timer _persistenceTimer;
    private readonly SemaphoreSlim _dbWriteLock = new(1, 1);

    public NzbProviderAffinityService(
        IServiceScopeFactory scopeFactory,
        ConfigManager configManager)
    {
        _scopeFactory = scopeFactory;
        _configManager = configManager;

        // Persist stats every 30 seconds
        _persistenceTimer = new Timer(PersistStats, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        // Load existing stats from database
        _ = Task.Run(LoadStatsAsync);
    }

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
    }

    /// <summary>
    /// Get the preferred provider index for an NZB based on performance history.
    /// Uses epsilon-greedy strategy: exploits best provider most of the time,
    /// but explores other providers 10% of the time to gather performance data.
    /// Returns null if no preference exists or affinity is disabled.
    /// </summary>
    public int? GetPreferredProvider(string jobName, int totalProviders = 0, bool logDecision = false)
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

        var candidates = eligibleProviders
            .Select(kvp => new
            {
                ProviderIndex = kvp.Key,
                Stats = kvp.Value,
                // Score: 20% weight on success rate, 80% weight on speed
                // Normalize both to 0-100 scale for fair comparison
                NormalizedSuccessRate = kvp.Value.SuccessRate, // Already 0-100
                NormalizedSpeed = (kvp.Value.AverageSpeedBps / (double)maxSpeed) * 100.0, // Normalize to 0-100
                Score = (kvp.Value.SuccessRate * 0.2) + ((kvp.Value.AverageSpeedBps / (double)maxSpeed) * 100.0 * 0.8)
            })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (candidates == null) return null;

        return candidates.ProviderIndex;
    }

    /// <summary>
    /// Get all provider statistics for a specific NZB
    /// </summary>
    public Dictionary<int, (double SuccessRate, long AverageSpeedBps, int SuccessfulSegments)> GetJobStats(string jobName)
    {
        if (!_stats.TryGetValue(jobName, out var jobStats))
            return new Dictionary<int, (double, long, int)>();

        return jobStats.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.SuccessRate, kvp.Value.AverageSpeedBps, kvp.Value.SuccessfulSegments)
        );
    }

    /// <summary>
    /// Clear statistics for a specific job (when queue item is deleted)
    /// </summary>
    public void ClearJobStats(string jobName)
    {
        _stats.TryRemove(jobName, out _);
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
}
