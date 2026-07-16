using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;
using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionPoolStats
{
    /// <summary>
    /// Floor on the gap between websocket pushes per provider. The connections UI is read by a
    /// human; 4 frames per second is past what anyone can follow, while the events driving it
    /// arrive thousands of times per second during playback.
    /// </summary>
    private const int MinPushIntervalMs = 250;

    private readonly int[] _live;
    private readonly int[] _idle;
    private readonly int _max;
    private int _totalLive;
    private int _totalIdle;
    private readonly UsenetProviderConfig _providerConfig;
    private readonly WebsocketManager _websocketManager;
    private readonly ConnectionPool<INntpClient>[] _connectionPools;

    // Per-provider push coalescing state, each guarded by its own lock.
    private readonly object[] _pushLocks;
    private readonly long[] _lastPushTimestamp;
    private readonly bool[] _flushScheduled;
    private readonly ConnectionPoolChangedEventArgs?[] _pendingArgs;

    public ConnectionPoolStats(UsenetProviderConfig providerConfig, WebsocketManager websocketManager)
    {
        var count = providerConfig.Providers.Count;
        _live = new int[count];
        _idle = new int[count];
        _connectionPools = new ConnectionPool<INntpClient>[count];
        _max = providerConfig.Providers
            .Where(x => x.Type == ProviderType.Pooled)
            .Select(x => x.MaxConnections)
            .Sum();

        _pushLocks = new object[count];
        for (var i = 0; i < count; i++) _pushLocks[i] = new object();
        _lastPushTimestamp = new long[count];
        _flushScheduled = new bool[count];
        _pendingArgs = new ConnectionPoolChangedEventArgs?[count];

        _providerConfig = providerConfig;
        _websocketManager = websocketManager;

        // Single registration slot, so the instance rebuilt on each config reload replaces the
        // previous one rather than stacking up.
        _websocketManager.RegisterStateRefresher(RefreshState);
    }

    public void RegisterConnectionPool(int providerIndex, ConnectionPool<INntpClient> connectionPool)
    {
        _connectionPools[providerIndex] = connectionPool;
    }

    public List<ConnectionUsageContext> GetActiveConnections()
    {
        var list = new List<ConnectionUsageContext>();
        foreach (var pool in _connectionPools)
        {
            if (pool != null)
                list.AddRange(pool.GetActiveConnections());
        }
        return list;
    }

    public Dictionary<int, List<ConnectionUsageContext>> GetActiveConnectionsByProvider()
    {
        var result = new Dictionary<int, List<ConnectionUsageContext>>();
        for (int i = 0; i < _connectionPools.Length; i++)
        {
            var pool = _connectionPools[i];
            if (pool != null)
                result[i] = pool.GetActiveConnections();
        }
        return result;
    }

    public EventHandler<ConnectionPoolChangedEventArgs> GetOnConnectionPoolChanged(int providerIndex)
    {
        return OnEvent;

        void OnEvent(object? _, ConnectionPoolChangedEventArgs args)
        {
            // Counters are plain integer work and every caller depends on them being current,
            // so they update on every event regardless of who is watching.
            if (_providerConfig.Providers[providerIndex].Type == ProviderType.Pooled)
            {
                lock (this)
                {
                    _live[providerIndex] = args.Live;
                    _idle[providerIndex] = args.Idle;
                    _totalLive = _live.Sum();
                    _totalIdle = _idle.Sum();
                }
            }

            // Everything past this point exists only to paint the connections UI, and building it
            // costs ~60 KB per call. This event fires on every connection borrow and return — twice
            // per segment, on 11 raise sites, fanned out to every provider's pool — so on a
            // multi-thousand-segment file it is the single largest allocator in the process. Skip
            // it outright when no websocket is attached; RefreshState publishes a current snapshot
            // if one attaches later, so the cached message never goes stale.
            if (!_websocketManager.HasSubscribers) return;

            SchedulePush(providerIndex, args);
        }
    }

    /// <summary>
    /// Coalesces pushes to at most one per <see cref="MinPushIntervalMs"/> per provider. The first
    /// event after a quiet period publishes immediately so the UI stays responsive; a burst
    /// collapses into a single trailing publish carrying the newest state. Per provider rather than
    /// global, because each provider's message is addressed to its own row in the UI.
    /// </summary>
    private void SchedulePush(int providerIndex, ConnectionPoolChangedEventArgs args)
    {
        ConnectionPoolChangedEventArgs? pushNow = null;

        lock (_pushLocks[providerIndex])
        {
            _pendingArgs[providerIndex] = args;

            // A trailing flush is already pending and will pick up what we just stored.
            if (_flushScheduled[providerIndex]) return;

            var sinceLastPush = Stopwatch.GetElapsedTime(_lastPushTimestamp[providerIndex]).TotalMilliseconds;
            if (sinceLastPush >= MinPushIntervalMs)
            {
                _lastPushTimestamp[providerIndex] = Stopwatch.GetTimestamp();
                _pendingArgs[providerIndex] = null;
                pushNow = args;
            }
            else
            {
                _flushScheduled[providerIndex] = true;
                _ = FlushAfterDelayAsync(providerIndex, (int)(MinPushIntervalMs - sinceLastPush));
            }
        }

        // Built outside the lock: serializing every active connection is not work to hold a lock for.
        if (pushNow != null) Push(providerIndex, pushNow);
    }

    private async Task FlushAfterDelayAsync(int providerIndex, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs).ConfigureAwait(false);

            ConnectionPoolChangedEventArgs? args;
            lock (_pushLocks[providerIndex])
            {
                args = _pendingArgs[providerIndex];
                _pendingArgs[providerIndex] = null;
                _flushScheduled[providerIndex] = false;
                _lastPushTimestamp[providerIndex] = Stopwatch.GetTimestamp();
            }

            if (args != null) Push(providerIndex, args);
        }
        catch (Exception e)
        {
            // A dropped UI frame must never surface on the streaming path that queued this.
            Log.Debug($"Connection-pool stats flush failed. {e.Message}");
        }
    }

    private void Push(int providerIndex, ConnectionPoolChangedEventArgs args)
    {
        // Get usage breakdown from all connection pools
        var usageBreakdown = GetGlobalUsageBreakdown();
        var providerBreakdown = GetProviderUsageBreakdown(providerIndex);

        // Get detailed connections for this provider to send over websocket
        var activeConns = _connectionPools[providerIndex]?.GetActiveConnections() ?? new List<ConnectionUsageContext>();
        var connsJson = JsonSerializer.Serialize(activeConns.Select(c => new {
            t = (int)c.UsageType,
            d = c.Details,
            jn = c.JobName,
            b = c.IsBackup,
            s = c.IsSecondary,
            bc = c.DetailsObject?.BufferedCount,
            ws = c.DetailsObject?.BufferWindowStart,
            we = c.DetailsObject?.BufferWindowEnd,
            ts = c.DetailsObject?.TotalSegments,
            i = c.DetailsObject?.DavItemId,
            bp = c.DetailsObject?.CurrentBytePosition,
            fs = c.DetailsObject?.FileSize
        }));

        var message = $"{providerIndex}|{args.Live}|{args.Idle}|{_totalLive}|{_max}|{_totalIdle}|{usageBreakdown}|{providerBreakdown}|{connsJson}";
        _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message);
    }

    /// <summary>
    /// Republishes every provider's current state. Invoked when a websocket subscriber connects,
    /// because while none was attached the events above published nothing and the cached message
    /// the new subscriber just replayed may describe connections that no longer exist.
    /// </summary>
    private void RefreshState()
    {
        foreach (var pool in _connectionPools)
            pool?.TriggerStatsUpdate();
    }

    private string GetGlobalUsageBreakdown()
    {
        var allUsageCounts = new Dictionary<ConnectionUsageType, int>();

        foreach (var pool in _connectionPools)
        {
            if (pool == null) continue;

            var breakdown = pool.GetUsageBreakdown();
            foreach (var (usageType, count) in breakdown)
            {
                allUsageCounts.TryGetValue(usageType, out var currentCount);
                allUsageCounts[usageType] = currentCount + count;
            }
        }

        var parts = allUsageCounts
            .OrderBy(x => x.Key)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToArray();

        return parts.Length > 0 ? string.Join(",", parts) : "none";
    }

    private string GetProviderUsageBreakdown(int providerIndex)
    {
        var pool = _connectionPools[providerIndex];
        if (pool == null) return "none";

        var breakdown = pool.GetUsageBreakdown();
        var parts = breakdown
            .OrderBy(x => x.Key)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToArray();

        return parts.Length > 0 ? string.Join(",", parts) : "none";
    }

    public sealed class ConnectionPoolChangedEventArgs(int live, int idle, int max) : EventArgs
    {
        public int Live { get; } = live;
        public int Idle { get; } = idle;
        public int Max { get; } = max;
        public int Active => Live - Idle;
    }
}