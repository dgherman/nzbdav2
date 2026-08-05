using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Sizes streaming memory from the process's own heap ceiling, so the defaults suit the box they
/// are running on without the operator configuring anything.
///
/// Why this exists (2026-07-28): the streaming defaults and the shipped GC limit were chosen
/// independently and could not coexist. The image ships <c>DOTNET_GCHeapHardLimit=0x20000000</c> —
/// 512 MB of managed heap, sized for a 1 GB VPS and applied regardless of host RAM — while the
/// streaming path defaulted to a 256 MB prefetch floor per stream, 8 concurrent streams each holding
/// a 32 MB ring, and 512 MB of segment-pool retention. Those were tuned against a NAS running a
/// 4 GiB limit. Reproduced in <c>docker/repro</c>: a single stream drove the heap to 490 MB of a
/// 512 MB limit and the runtime aborted the process with a bare "Out of memory.", which killed the
/// backend and took the container with it.
///
/// Every value here is derived from one measured number, so raising the heap limit raises them all
/// together and lowering it lowers them all together.
/// </summary>
public static class MemoryBudget
{
    private const long Mb = 1024 * 1024;

    /// <summary>
    /// Each in-flight segment is resident twice: once in the pooled destination buffer, and once in
    /// the copy System.IO.Pipelines holds inside <c>BufferToEndStream</c>. Measured on the repro
    /// harness — a window reported as "holds~255MB data" drove a 490 MB heap.
    /// </summary>
    public const int ResidentBytesPerDataByte = 2;

    /// <summary>Ring buffer held by each shared-stream entry, i.e. by each concurrent stream slot.</summary>
    public const long RingBufferBytes = 32 * Mb;

    /// <summary>
    /// Share of the heap that all buffered streams together may hold as prefetched *data*. Doubled by
    /// <see cref="ResidentBytesPerDataByte"/> once resident, so this is 40% of the heap in practice,
    /// leaving room for the rings, the pool's retention and the rest of the process.
    /// </summary>
    private const double StreamingDataShare = 0.20;

    /// <summary>Share of the heap the segment pool may hold idle, waiting to be reused.</summary>
    private const double PoolIdleShare = 0.15;

    /// <summary>
    /// The production-validated window from v0.11.5 (256 MB of data). A larger box buys more streams
    /// and more headroom rather than a larger window: the window is sized for refill latency, and
    /// past this it only holds memory.
    /// </summary>
    private const long MaxPerStreamPrefetchBytes = 256 * Mb;

    /// <summary>
    /// Below this a stream starves and trips circuit breakers (measured in PR #21). On a box too
    /// small to afford it, the honest answer is fewer streams, not a window that cannot keep up.
    /// </summary>
    private const long MinPerStreamPrefetchBytes = 16 * Mb;

    private const long MinPoolIdleBytes = 32 * Mb;
    private const long MaxPoolIdleBytes = 512 * Mb;

    /// <summary>Heap per concurrent stream slot before another slot is affordable.</summary>
    private const long HeapPerStreamSlot = 512 * Mb;

    private const int MinConcurrentStreams = 2;
    private const int MaxConcurrentStreamSlots = 8;

    /// <summary>Used when the runtime reports nothing usable — deliberately pessimistic.</summary>
    private const long FallbackHeapLimitBytes = 512 * Mb;

    private static readonly Lazy<long> LazyHeapLimit = new(DetectHeapLimitBytes);

    /// <summary>The managed-heap ceiling this process is actually running under.</summary>
    public static long HeapLimitBytes => LazyHeapLimit.Value;

    /// <summary>
    /// Prefetched data one stream may hold, in bytes. Rounded down to 8 MB so the derived window is
    /// a stable, readable number rather than an artefact of the division.
    /// </summary>
    public static long PerStreamPrefetchBytes(long heapLimitBytes, int maxConcurrentStreams)
    {
        if (heapLimitBytes <= 0) heapLimitBytes = FallbackHeapLimitBytes;
        if (maxConcurrentStreams < 1) maxConcurrentStreams = 1;

        var share = (long)(heapLimitBytes * StreamingDataShare) / maxConcurrentStreams;
        var rounded = share / (8 * Mb) * (8 * Mb);
        return Math.Clamp(rounded, MinPerStreamPrefetchBytes, MaxPerStreamPrefetchBytes);
    }

    /// <summary>
    /// How many buffered streams may run at once. Each slot costs a ring plus a full prefetch window,
    /// so on a small heap the cap is what stops slot count alone from exhausting it.
    /// </summary>
    public static int MaxConcurrentStreams(long heapLimitBytes)
    {
        if (heapLimitBytes <= 0) heapLimitBytes = FallbackHeapLimitBytes;
        var affordable = (int)(heapLimitBytes / HeapPerStreamSlot);
        return Math.Clamp(affordable, MinConcurrentStreams, MaxConcurrentStreamSlots);
    }

    /// <summary>
    /// Share of the heap RAR header extraction may hold while a queue item imports. Smaller than
    /// <see cref="StreamingDataShare"/> on purpose: an import runs alongside playback and must not
    /// compete with the prefetch window for the same heap.
    /// </summary>
    private const double ImportHeaderDataShare = 0.10;

    /// <summary>
    /// Never derive fewer than the three volumes the old fixed budget allowed, so this can only
    /// ever raise import parallelism, never lower it.
    /// </summary>
    private const int MinConcurrentRarHeaderParts = 3;

    /// <summary>
    /// How many RAR volumes may have their headers parsed at once before the heap, rather than the
    /// connection budget, becomes the limit.
    ///
    /// Header parsing reads unbuffered, so a volume holds roughly one in-flight article — two
    /// across the seek from the front of the volume to its end-archive header — each resident
    /// <see cref="ResidentBytesPerDataByte"/> times over. That makes the cost scale with segment
    /// size, which is why this takes bytes rather than a volume count: a release posted with 4 MB
    /// segments costs five times a 750 KB one for the same parallelism.
    /// </summary>
    public static int MaxConcurrentRarHeaderParts(long heapLimitBytes, long residentBytesPerPart)
    {
        if (heapLimitBytes <= 0) heapLimitBytes = FallbackHeapLimitBytes;
        if (residentBytesPerPart <= 0) return MinConcurrentRarHeaderParts;

        var budget = (long)(heapLimitBytes * ImportHeaderDataShare);
        var affordable = (int)(budget / residentBytesPerPart);
        return Math.Max(MinConcurrentRarHeaderParts, affordable);
    }

    /// <summary>Bytes the segment buffer pool may retain idle, rounded down to whole MB.</summary>
    public static long PoolIdleCapBytes(long heapLimitBytes)
    {
        if (heapLimitBytes <= 0) heapLimitBytes = FallbackHeapLimitBytes;
        var share = (long)(heapLimitBytes * PoolIdleShare) / Mb * Mb;
        return Math.Clamp(share, MinPoolIdleBytes, MaxPoolIdleBytes);
    }

    /// <summary>
    /// The GC's configured hard limit if there is one, otherwise what it reports as available.
    /// <c>GC.GetConfigurationVariables</c> is the authoritative source for the limit and, unlike
    /// <c>GCMemoryInfo.TotalAvailableMemoryBytes</c>, does not vary with which collection last ran —
    /// reading the latter under load reported 512 MB on a process configured for 2032 MB.
    /// </summary>
    private static long DetectHeapLimitBytes()
    {
        try
        {
            var config = GC.GetConfigurationVariables();

            if (config.TryGetValue("GCHeapHardLimit", out var hardLimit))
            {
                var bytes = ToInt64(hardLimit);
                if (bytes > 0) return bytes;
            }

            // No absolute limit, but possibly a percentage of the container/physical memory.
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (config.TryGetValue("GCHeapHardLimitPercent", out var percent))
            {
                var pct = ToInt64(percent);
                if (pct > 0 && available > 0) return available * pct / 100;
            }

            if (available > 0) return available;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[MemoryBudget] Could not read the GC configuration; falling back to {FallbackMB}MB.",
                FallbackHeapLimitBytes / Mb);
        }

        return FallbackHeapLimitBytes;
    }

    private static long ToInt64(object value) => value switch
    {
        long l => l,
        ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
        int i => i,
        uint ui => ui,
        string s when long.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };

    /// <summary>
    /// One line, at startup, saying what the box affords. The whole failure this replaces was
    /// invisible until someone correlated a crash with a constant in a Dockerfile.
    /// </summary>
    public static void LogBudget()
    {
        var streams = MaxConcurrentStreams(HeapLimitBytes);
        // Keep this the ONLY place the literal "[MemoryBudget] Heap limit" appears: the startup banner
        // used to quote it, so every grep for the real line also matched the banner.
        Log.Warning("[MemoryBudget] Heap limit {HeapMB}MB -> {Streams} concurrent streams, " +
                    "{PerStreamMB}MB prefetch data each (~{ResidentMB}MB resident), {PoolMB}MB pool retention. " +
                    "Override with usenet.prefetch-window / usenet.max-concurrent-buffered-streams / NZBDAV_SEGMENT_POOL_MAX_IDLE_MB.",
            HeapLimitBytes / Mb,
            streams,
            PerStreamPrefetchBytes(HeapLimitBytes, streams) / Mb,
            PerStreamPrefetchBytes(HeapLimitBytes, streams) * ResidentBytesPerDataByte / Mb,
            PoolIdleCapBytes(HeapLimitBytes) / Mb);
    }
}
