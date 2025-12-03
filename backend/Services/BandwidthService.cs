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
        var json = JsonSerializer.Serialize(snapshots);
        _websocketManager.SendMessage(WebsocketTopic.Bandwidth, json);
    }

    private async void PersistStats(object? state)
    {
        if (!_configManager.IsStatsEnabled()) return;

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
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to persist bandwidth stats");
        }
    }

    public void RecordBytes(int providerIndex, long bytes)
    {
        if (!_configManager.IsStatsEnabled()) return;

        var stats = _providerStats.GetOrAdd(providerIndex, _ => new ProviderStats());
        stats.AddBytes(bytes);
    }

    public List<ProviderBandwidthSnapshot> GetBandwidthStats()
    {
        var snapshots = new List<ProviderBandwidthSnapshot>();
        foreach (var (index, stats) in _providerStats)
        {
            snapshots.Add(stats.GetSnapshot(index));
        }
        return snapshots;
    }

    private class ProviderStats
    {
        private long _totalBytes;
        private long _pendingDbBytes;
        private readonly object _lock = new();
        
        // Optimization: Track bandwidth in 1-second buckets instead of storing every read event.
        // This reduces AddBytes from O(N) to O(1) and removes GC pressure.
        private long _currentBucketBytes;
        private readonly Queue<long> _historyBuckets = new();
        
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
                // Rotate the bucket
                _historyBuckets.Enqueue(_currentBucketBytes);
                _currentBucketBytes = 0;

                // Keep only last 5 seconds
                while (_historyBuckets.Count > 5)
                {
                    _historyBuckets.Dequeue();
                }

                var recentBytes = _historyBuckets.Sum();
                // Average over the window (5s), or actual elapsed if less than 5s to avoid slow startup ramp-up
                var duration = Math.Max(1, _historyBuckets.Count); 
                
                // Calculate speed (bytes per second)
                var speed = (long)(recentBytes / (double)duration);
                
                return new ProviderBandwidthSnapshot
                {
                    ProviderIndex = index,
                    TotalBytes = _totalBytes,
                    CurrentSpeed = speed
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
}