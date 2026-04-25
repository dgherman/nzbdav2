using System.Collections.Concurrent;
using Prometheus;

namespace NzbWebDAV.Metrics;

/// <summary>
/// Central registry for application-level Prometheus metrics.
/// Exposed at <c>/metrics</c>.
/// </summary>
public static class AppMetrics
{
    // ---- Shared streams ------------------------------------------------------

    public static readonly Counter SharedStreamHits = Prometheus.Metrics
        .CreateCounter(
            "nzbdav_shared_stream_hits_total",
            "Number of GETs that attached to an existing shared stream.",
            new CounterConfiguration { LabelNames = new[] { "path" } });

    public static readonly Counter SharedStreamMisses = Prometheus.Metrics
        .CreateCounter(
            "nzbdav_shared_stream_misses_total",
            "Number of GETs that had to create a new stream (no shared entry, or position out of range).",
            new CounterConfiguration { LabelNames = new[] { "path", "reason" } });

    public static readonly Gauge SharedStreamActiveEntries = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_shared_stream_active_entries",
            "Number of currently registered shared-stream entries.");

    public static readonly Gauge SharedStreamActiveReaders = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_shared_stream_active_readers",
            "Number of readers currently attached across all shared streams.");

    // ---- Connection pool -----------------------------------------------------

    public static readonly Gauge PoolLiveConnections = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_pool_live_connections",
            "Number of NNTP connections currently alive in the pool.",
            new GaugeConfiguration { LabelNames = new[] { "pool" } });

    public static readonly Gauge PoolIdleConnections = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_pool_idle_connections",
            "Number of idle (parked) NNTP connections.",
            new GaugeConfiguration { LabelNames = new[] { "pool" } });

    public static readonly Gauge PoolActiveConnections = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_pool_active_connections",
            "Number of NNTP connections currently checked out and in use.",
            new GaugeConfiguration { LabelNames = new[] { "pool" } });

    public static readonly Gauge PoolMaxConnections = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_pool_max_connections",
            "Configured upper bound for the pool.",
            new GaugeConfiguration { LabelNames = new[] { "pool" } });

    public static readonly Gauge PoolRemainingSlots = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_pool_remaining_semaphore_slots",
            "Remaining semaphore slots before requests start queueing.",
            new GaugeConfiguration { LabelNames = new[] { "pool" } });

    public static readonly Gauge PoolConsecutiveFailures = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_pool_consecutive_connection_failures",
            "Consecutive connection establishment failures (resets on success).",
            new GaugeConfiguration { LabelNames = new[] { "pool" } });

    public static readonly Gauge PoolCircuitBreakerTripped = Prometheus.Metrics
        .CreateGauge(
            "nzbdav_pool_circuit_breaker_tripped",
            "1 if circuit breaker is currently tripped, 0 otherwise.",
            new GaugeConfiguration { LabelNames = new[] { "pool" } });

    // ---- Seek latency --------------------------------------------------------

    public static readonly Histogram SeekLatencySeconds = Prometheus.Metrics
        .CreateHistogram(
            "nzbdav_seek_latency_seconds",
            "Time spent inside NzbFileStream.Seek().",
            new HistogramConfiguration
            {
                LabelNames = new[] { "kind" }, // cold | warm | noop
                // 1ms .. 5s, sensible for both warm (sub-ms) and cold (hundreds of ms) seeks
                Buckets = Histogram.ExponentialBuckets(start: 0.001, factor: 2.0, count: 14)
            });

    // ---- Provider snapshot registration --------------------------------------

    /// <summary>
    /// Snapshot interface implemented by ConnectionPool so the background
    /// collector can read its live counters without taking a hard dependency
    /// on the pool's generic type.
    /// </summary>
    public interface IPoolSnapshotProvider
    {
        string PoolName { get; }
        int LiveConnections { get; }
        int IdleConnections { get; }
        int ActiveConnections { get; }
        int MaxConnections { get; }
        int RemainingSemaphoreSlots { get; }
        int ConsecutiveFailures { get; }
        bool IsCircuitBreakerTripped { get; }
    }

    private static readonly ConcurrentDictionary<string, IPoolSnapshotProvider> s_pools = new();

    public static void RegisterPool(IPoolSnapshotProvider pool)
    {
        s_pools[pool.PoolName] = pool;
    }

    public static void UnregisterPool(string poolName)
    {
        s_pools.TryRemove(poolName, out _);
        PoolLiveConnections.RemoveLabelled(poolName);
        PoolIdleConnections.RemoveLabelled(poolName);
        PoolActiveConnections.RemoveLabelled(poolName);
        PoolMaxConnections.RemoveLabelled(poolName);
        PoolRemainingSlots.RemoveLabelled(poolName);
        PoolConsecutiveFailures.RemoveLabelled(poolName);
        PoolCircuitBreakerTripped.RemoveLabelled(poolName);
    }

    /// <summary>Refresh all pool gauges from registered snapshot providers.</summary>
    public static void RefreshPoolGauges()
    {
        foreach (var pool in s_pools.Values)
        {
            var name = pool.PoolName;
            PoolLiveConnections.WithLabels(name).Set(pool.LiveConnections);
            PoolIdleConnections.WithLabels(name).Set(pool.IdleConnections);
            PoolActiveConnections.WithLabels(name).Set(pool.ActiveConnections);
            PoolMaxConnections.WithLabels(name).Set(pool.MaxConnections);
            PoolRemainingSlots.WithLabels(name).Set(pool.RemainingSemaphoreSlots);
            PoolConsecutiveFailures.WithLabels(name).Set(pool.ConsecutiveFailures);
            PoolCircuitBreakerTripped.WithLabels(name).Set(pool.IsCircuitBreakerTripped ? 1 : 0);
        }
    }
}
