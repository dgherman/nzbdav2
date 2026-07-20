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

    // Hits and misses are accounted at exactly one site — SharedStreamManager.TryAttach — so
    // hits + misses == attach attempts, always. They were previously incremented in TryAttach AND
    // again in GetOrCreate's fast path for the same request, which made a single failed attach
    // appear once as "position_out_of_range" and once as "existing_entry_unattachable". That made
    // one failure mode look like two independent ones and doubled the apparent miss count; see the
    // paired counts recorded on issue #18. Do not add increments to the create path — it reports
    // through SharedStreamCreates below.
    public static readonly Counter SharedStreamHits = Prometheus.Metrics
        .CreateCounter(
            "nzbdav_shared_stream_hits_total",
            "Number of GETs that attached to an existing shared stream.");

    // No per-path label: file paths are unbounded-cardinality and would create
    // one permanent time series per streamed file.
    public static readonly Counter SharedStreamMisses = Prometheus.Metrics
        .CreateCounter(
            "nzbdav_shared_stream_misses_total",
            "Number of GETs that had to create a new stream (no shared entry, or position out of range).",
            new CounterConfiguration { LabelNames = new[] { "reason" } });

    /// <summary>
    /// Outcomes of the create path, which runs only after TryAttach has already missed.
    /// Separate from hits/misses so the create path can never double-count an attach attempt.
    /// </summary>
    public static readonly Counter SharedStreamCreates = Prometheus.Metrics
        .CreateCounter(
            "nzbdav_shared_stream_creates_total",
            "Outcomes of shared-stream entry creation attempts.",
            new CounterConfiguration { LabelNames = new[] { "outcome" } });

    /// <summary>
    /// Signed distance between a rejected reader's start position and the entry's write frontier,
    /// bucketed by magnitude. This is the measurement that discriminates the candidate fixes for
    /// issue #18: misses clustered under ~100 MB would be converted by a larger ring buffer, whereas
    /// misses in the GB range (a player reading the Matroska tail/Cues) can only be served by giving
    /// the file more than one pumped entry. Direction is labelled because they mean different things
    /// — "ahead" is a seek past the pump, "behind" is a reader that fell out of the window.
    /// </summary>
    public static readonly Histogram SharedStreamAttachMissDistanceBytes = Prometheus.Metrics
        .CreateHistogram(
            "nzbdav_shared_stream_attach_miss_distance_bytes",
            "Absolute distance from the write frontier for readers that failed to attach.",
            new HistogramConfiguration
            {
                LabelNames = new[] { "direction" },
                // 1 MB to ~64 GB. The ring is 32 MB, so the 32/64/128 MB boundaries straddle
                // "a bigger ring would have caught this".
                Buckets = new double[]
                {
                    1L << 20, 4L << 20, 16L << 20, 32L << 20, 64L << 20, 128L << 20,
                    512L << 20, 1L << 30, 4L << 30, 16L << 30, 64L << 30
                }
            });

    /// <summary>
    /// How long entries survive, and what ended them. The issue #18 write-up read
    /// <c>active_entries == 0</c> under live load as evidence that entries die early, but that
    /// compared an instantaneous gauge against cumulative counters. This measures lifetime directly.
    /// </summary>
    public static readonly Histogram SharedStreamEntryLifetimeSeconds = Prometheus.Metrics
        .CreateHistogram(
            "nzbdav_shared_stream_entry_lifetime_seconds",
            "Lifetime of a shared-stream entry, from creation to teardown.",
            new HistogramConfiguration
            {
                LabelNames = new[] { "cause" },
                Buckets = new double[] { 0.5, 1, 5, 10, 30, 60, 300, 900, 3600 }
            });

    /// <summary>
    /// Readers served over an entry's whole life, including the creating reader. An entry that only
    /// ever serves 1 shared nothing — it paid a 32 MB ring and a scarce stream slot to do the work a
    /// private BufferedSegmentStream would have done.
    /// </summary>
    public static readonly Histogram SharedStreamEntryReadersServed = Prometheus.Metrics
        .CreateHistogram(
            "nzbdav_shared_stream_entry_readers_served",
            "Total readers attached over the lifetime of a shared-stream entry.",
            new HistogramConfiguration
            {
                Buckets = new double[] { 1, 2, 3, 5, 8, 16, 32 }
            });

    /// <summary>Bytes an entry's pump actually moved before teardown.</summary>
    public static readonly Histogram SharedStreamEntryBytesPumped = Prometheus.Metrics
        .CreateHistogram(
            "nzbdav_shared_stream_entry_bytes_pumped",
            "Bytes pumped into the ring buffer over the lifetime of a shared-stream entry.",
            new HistogramConfiguration
            {
                Buckets = new double[]
                {
                    1L << 20, 16L << 20, 64L << 20, 256L << 20, 1L << 30, 4L << 30, 16L << 30
                }
            });

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
