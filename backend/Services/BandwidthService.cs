using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public class BandwidthService
{
    private readonly WebsocketManager _websocketManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<int, ProviderStats> _providerStats = new();
    private readonly Timer _broadcastTimer;
    private readonly Timer _persistenceTimer;
    private readonly SemaphoreSlim _dbWriteLock = new(1, 1); // Serialize DB writes to prevent SQLite locks
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BandwidthService(
        WebsocketManager websocketManager, 
        IServiceScopeFactory scopeFactory,
        ConfigManager configManager)
    {
        _websocketManager = websocketManager;
        _scopeFactory = scopeFactory;
        _configManager = configManager;
        _broadcastTimer = new Timer(BroadcastStats, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        _persistenceTimer = new Timer(PersistStats, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void BroadcastStats(object? state)
    {
        if (!_configManager.IsStatsEnabled()) return;

        var snapshots = GetBandwidthStats();
        var json = JsonSerializer.Serialize(snapshots, _jsonOptions);
        _websocketManager.SendMessage(WebsocketTopic.Bandwidth, json);
    }

    private async void PersistStats(object? state)
    {
        if (!_configManager.IsStatsEnabled()) return;

        const int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await _dbWriteLock.WaitAsync();
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
                    var now = DateTimeOffset.UtcNow;

                    var hasChanges = false;
                    foreach (var (index, stats) in _providerStats)
                    {
                        var bytes = stats.GetAndResetPendingDbBytes();
                        if (bytes > 0)
                        {
                            dbContext.BandwidthSamples.Add(new BandwidthSample
                            {
                                ProviderIndex = index,
                                Timestamp = now,
                                Bytes = bytes
                            });
                            hasChanges = true;
                        }
                    }

                    if (hasChanges)
                    {
                        await dbContext.SaveChangesAsync().ConfigureAwait(false);
                    }
                    break; // Success
                }
                finally
                {
                    _dbWriteLock.Release();
                }
            }
            catch (Exception e) when (i < maxRetries - 1 && e.Message.Contains("database is locked"))
            {
                Log.Warning($"[BandwidthService] Database locked on attempt {i + 1}/{maxRetries}, retrying...");
                await Task.Delay(100 * (i + 1)); // Exponential backoff: 100ms, 200ms, 300ms
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to persist bandwidth stats");
                break;
            }
        }
    }

    public void RecordBytes(int providerIndex, long bytes)
    {
        if (!_configManager.IsStatsEnabled()) return;

        var stats = _providerStats.GetOrAdd(providerIndex, _ => new ProviderStats());
        stats.AddBytes(bytes);
    }

    public void RecordLatency(int providerIndex, int latencyMs)
    {
        if (!_configManager.IsStatsEnabled()) return;

        var stats = _providerStats.GetOrAdd(providerIndex, _ => new ProviderStats());
        stats.AddLatency(latencyMs);
    }

    public long GetAverageLatency(int providerIndex)
    {
        if (!_configManager.IsStatsEnabled()) return 0;

        if (_providerStats.TryGetValue(providerIndex, out var stats))
        {
            return stats.GetAverageLatency();
        }
        return 0;
    }

    public List<ProviderBandwidthSnapshot> GetBandwidthStats()
    {
        var config = _configManager.GetUsenetProviderConfig();
        var snapshots = new List<ProviderBandwidthSnapshot>();
        foreach (var (index, stats) in _providerStats)
        {
            var snapshot = stats.GetSnapshot(index);
            if (index >= 0 && index < config.Providers.Count)
            {
                snapshot.Host = config.Providers[index].Host;
            }
            snapshots.Add(snapshot);
        }
        return snapshots;
    }

    private class ProviderStats
    {
        private long _totalBytes;
        private long _pendingDbBytes;
        private readonly object _lock = new();
        
        // Optimization: Track bandwidth in 1-second buckets instead of storing every read event.
        private long _currentBucketBytes;
        private readonly Queue<long> _historyBuckets = new();

        // Latency tracking (Exponential Moving Average)
        private double _latencyEma;
        private const double Alpha = 0.05; // Smoothing factor (lower = smoother)
        
        public long GetAverageLatency()
        {
            lock (_lock)
            {
                return (long)_latencyEma;
            }
        }

        public void AddLatency(int ms)
        {
            lock (_lock)
            {
                if (_latencyEma == 0)
                {
                    _latencyEma = ms;
                }
                else
                {
                    _latencyEma = (ms * Alpha) + (_latencyEma * (1.0 - Alpha));
                }
            }
        }

        public void AddBytes(long bytes)
        {
            lock (_lock)
            {
                _totalBytes += bytes;
                _pendingDbBytes += bytes;
                _currentBucketBytes += bytes;
            }
        }

        public long GetAndResetPendingDbBytes()
        {
            lock (_lock)
            {
                var bytes = _pendingDbBytes;
                _pendingDbBytes = 0;
                return bytes;
            }
        }

        public ProviderBandwidthSnapshot GetSnapshot(int index)
        {
            lock (_lock)
            {
                // Rotate the buckets for bandwidth
                _historyBuckets.Enqueue(_currentBucketBytes);
                _currentBucketBytes = 0;

                // Keep only last 15 seconds (smoothed)
                while (_historyBuckets.Count > 15)
                {
                    _historyBuckets.Dequeue();
                }

                var recentBytes = _historyBuckets.Sum();
                // Average over the window, or actual elapsed if less than window to avoid slow startup ramp-up
                var duration = Math.Max(1, _historyBuckets.Count); 
                
                // Calculate speed (bytes per second)
                var speed = (long)(recentBytes / (double)duration);

                return new ProviderBandwidthSnapshot
                {
                    ProviderIndex = index,
                    TotalBytes = _totalBytes,
                    CurrentSpeed = speed,
                    AverageLatency = (long)_latencyEma
                };
            }
        }
    }
}

public class ProviderBandwidthSnapshot
{
    public int ProviderIndex { get; set; }
    public long TotalBytes { get; set; }
    public long CurrentSpeed { get; set; }
    public long AverageLatency { get; set; }
    public string? Host { get; set; }
}