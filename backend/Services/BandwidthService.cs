using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public class BandwidthService
{
    private readonly WebsocketManager _websocketManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<int, ProviderStats> _providerStats = new();
    private readonly Timer _broadcastTimer;
    private readonly Timer _persistenceTimer;

    public BandwidthService(WebsocketManager websocketManager, IServiceScopeFactory scopeFactory)
    {
        _websocketManager = websocketManager;
        _scopeFactory = scopeFactory;
        _broadcastTimer = new Timer(BroadcastStats, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        _persistenceTimer = new Timer(PersistStats, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void BroadcastStats(object? state)
    {
        var snapshots = GetBandwidthStats();
        var json = JsonSerializer.Serialize(snapshots);
        _websocketManager.SendMessage(WebsocketTopic.Bandwidth, json);
    }

    private async void PersistStats(object? state)
    {
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
        
        // Track last 5 seconds for "Current Speed" smoothing
        private readonly Queue<(DateTimeOffset time, long bytes)> _recentReads = new();
        
        public void AddBytes(long bytes)
        {
            lock (_lock)
            {
                _totalBytes += bytes;
                _pendingDbBytes += bytes;
                _recentReads.Enqueue((DateTimeOffset.UtcNow, bytes));
                CleanupOldReads();
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

        private void CleanupOldReads()
        {
            var threshold = DateTimeOffset.UtcNow.AddSeconds(-5);
            while (_recentReads.TryPeek(out var item) && item.time < threshold)
            {
                _recentReads.Dequeue();
            }
        }

        public ProviderBandwidthSnapshot GetSnapshot(int index)
        {
            lock (_lock)
            {
                CleanupOldReads();
                var recentBytes = _recentReads.Sum(x => x.bytes);
                var duration = 5.0;
                
                // Calculate speed (bytes per second)
                var speed = (long)(recentBytes / duration);
                
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