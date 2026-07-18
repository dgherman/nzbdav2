using System.Collections.Concurrent;
using System.Diagnostics;

namespace NzbWebDAV.Streams;

/// <summary>
/// A dedicated pool for segment buffers, replacing <see cref="System.Buffers.ArrayPool{T}.Shared"/>
/// on the streaming hot path (issue #23).
///
/// <para><b>Why not ArrayPool.</b> Two measured properties of <c>ArrayPool&lt;byte&gt;</c> — both the
/// Shared instance and <c>ArrayPool.Create</c> — make it the wrong fit for 0.7–8 MB segment buffers:</para>
/// <list type="number">
/// <item><description><b>Power-of-two rounding.</b> Buckets are sized <c>16 &lt;&lt; i</c>, so a 4.19 MB
/// segment is served an 8 MB array — 3.8 MB wasted per in-flight buffer, ~460 MB across a 90-segment
/// window. Setting <c>maxArrayLength</c> to the exact segment size does not help; the top bucket is
/// still the next power of two (verified empirically).</description></item>
/// <item><description><b>Retention is bounded by count, not bytes.</b> Shared caches per-thread plus
/// per-core stacks and only trims under memory pressure measured against the <i>cgroup</i> limit, not
/// <c>DOTNET_GCHeapHardLimit</c> — so at 3 GB used it felt no pressure and held ~770 MB of idle 8 MB
/// arrays that a forced compacting gen2 could not reclaim. <c>ArrayPool.Create(maxLen, perBucket)</c>
/// does not fix this either: <c>perBucket</c> is an array count, and across ~20 doubling buckets it
/// permits ~2.5 GB — worse than the residue it was meant to replace.</description></item>
/// </list>
///
/// <para><b>What this does instead.</b> Size classes are multiples of <see cref="Granularity"/>
/// rather than powers of two, so waste per buffer is under one granule instead of up to 3.8 MB; and
/// idle retention is capped in <i>bytes</i>, an absolute bound independent of core count, thread count
/// and the container's memory view. Buffers returned over the cap are dropped for the GC.</para>
/// </summary>
public sealed class SegmentBufferPool
{
    /// <summary>
    /// Size-class width. Every pooled buffer is a multiple of this, so the worst-case waste on any
    /// rent is one granule minus one byte. 256 KB keeps the class count small (~64 classes across the
    /// 0–16 MB range) while cutting the 4.19 MB → 8 MB rounding to 4.19 MB → 4.25 MB.
    /// </summary>
    public const int Granularity = 256 * 1024;

    private const int DefaultMaxBufferSize = 16 * 1024 * 1024;
    private const long DefaultMaxIdleBytes = 512L * 1024 * 1024;

    /// <summary>
    /// How long a size class must go unrented before its idle buffers may be reclaimed for another
    /// class. Long enough that a paused or seeking stream keeps its buffers, short enough that a
    /// finished stream stops occupying the cap.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>How often the rent path may run an opportunistic stale trim.</summary>
    public static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(10);

    private readonly int _maxBufferSize;
    private readonly long _maxIdleBytes;
    private readonly Func<long> _nowTicks;
    private readonly ConcurrentDictionary<int, SizeClass> _sizeClasses = new();
    private long _idleBytes;
    private long _trimmedBytes;    // released back to the GC because their class went idle
    private long _pressureTrims;   // trims triggered by a return that would otherwise be dropped
    private long _lastTrimTicks;

    /// <summary>
    /// The idle buffers of one size class, plus when that class was last rented from. The timestamp
    /// is what stops a finished stream's class from squatting on the cap: measured in production with
    /// two concurrent streams, a movie that had stopped ~15 minutes earlier still held 207 idle 768 KB
    /// buffers — 155 MB, 30% of the whole cap — while the 4.25 MB class serving the *live* streams was
    /// squeezed to 79 and the reuse rate fell below what ArrayPool had managed. The cap was doing its
    /// job; the space was simply in the wrong class.
    /// </summary>
    private sealed class SizeClass
    {
        public readonly ConcurrentStack<byte[]> Idle = new();
        public long LastRentTicks;
    }

    /// <summary>
    /// The process-wide pool used by <see cref="BufferedSegmentStream"/>. The idle cap is overridable
    /// via <c>NZBDAV_SEGMENT_POOL_MAX_IDLE_MB</c> so it can be tuned against
    /// <c>POST /api/gc-diagnostics</c> without a rebuild.
    /// </summary>
    public static readonly SegmentBufferPool Shared = new(DefaultMaxBufferSize, ReadMaxIdleBytesFromEnvironment());

    /// <param name="nowTicks">Clock seam, in <see cref="Stopwatch"/> ticks. Tests inject a controllable
    /// clock so staleness can be exercised without sleeping.</param>
    public SegmentBufferPool(int maxBufferSize, long maxIdleBytes, Func<long>? nowTicks = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferSize, Granularity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxIdleBytes);
        _maxBufferSize = maxBufferSize;
        _maxIdleBytes = maxIdleBytes;
        _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
    }

    /// <summary>Bytes currently held in the pool but not checked out. Bounded by the idle cap.</summary>
    public long IdleBytes => Interlocked.Read(ref _idleBytes);

    /// <summary>The absolute ceiling on <see cref="IdleBytes"/>.</summary>
    public long MaxIdleBytes => _maxIdleBytes;

    /// <summary>Trims triggered by cap pressure rather than by the opportunistic rent-path check.
    /// Zero means the pool never actually ran out of room — worth knowing before tuning the cap.</summary>
    public long PressureTrims => Interlocked.Read(ref _pressureTrims);

    /// <summary>Bytes released back to the GC because their class went idle.</summary>
    public long TrimmedBytes => Interlocked.Read(ref _trimmedBytes);

    /// <summary>
    /// Rents a buffer of at least <paramref name="minimumLength"/> bytes. Requests above the pool's
    /// maximum buffer size are satisfied with an unpooled allocation rather than distorting the pool
    /// with a class that will never be reused.
    /// </summary>
    public byte[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0) return [];

        // Oversize: allocate directly. Return() will recognise it as unpoolable and drop it.
        if (minimumLength > _maxBufferSize) return new byte[minimumLength];

        var sizeClass = RoundUpToSizeClass(minimumLength);
        var state = _sizeClasses.GetOrAdd(sizeClass, _ => new SizeClass { LastRentTicks = _nowTicks() });
        // Marks this class as live, which is what protects its idle buffers from eviction and marks
        // every staler class as a candidate.
        Volatile.Write(ref state.LastRentTicks, _nowTicks());

        MaybeTrimStale();

        if (state.Idle.TryPop(out var pooled))
        {
            Interlocked.Add(ref _idleBytes, -sizeClass);
            return pooled;
        }

        return new byte[sizeClass];
    }

    /// <summary>
    /// Returns a buffer to the pool. Buffers that did not come from this pool, or that would push
    /// idle retention past the cap, are silently dropped for the GC — that drop is the mechanism that
    /// makes the byte cap absolute.
    /// </summary>
    public void Return(byte[] buffer)
    {
        if (buffer is null) return;

        var length = buffer.Length;
        // A length that is not a whole number of granules was never handed out by this pool
        // (Rent always allocates a size-class multiple), so it has no class to go back into.
        if (length == 0 || length > _maxBufferSize || length % Granularity != 0) return;

        // Reserve capacity before publishing the buffer, so concurrent returns can never
        // collectively overshoot the cap. At capacity, try to make room by discarding buffers from
        // classes staler than this one before giving up — the cap stays absolute either way, but the
        // space it bounds follows demand instead of whichever stream happened to fill it first.
        if (!TryReserve(length))
        {
            // At capacity. Reclaim whatever has gone stale and try once more; if nothing is stale,
            // every class is live and the cap is genuinely full, so this buffer goes to the GC.
            if (TrimStale() == 0) return;
            Interlocked.Increment(ref _pressureTrims);
            if (!TryReserve(length)) return;
        }

        _sizeClasses.GetOrAdd(length, _ => new SizeClass { LastRentTicks = _nowTicks() }).Idle.Push(buffer);
    }

    private bool TryReserve(int length)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _idleBytes);
            if (current + length > _maxIdleBytes) return false;
            if (Interlocked.CompareExchange(ref _idleBytes, current + length, current) == current) return true;
        }
    }

    /// <summary>
    /// Releases every idle buffer belonging to a class that has gone unrented for
    /// <see cref="StaleAfter"/>, handing the memory back to the GC.
    ///
    /// <para>Eviction alone is not enough, because it is <i>demand-driven</i> — it only runs when a
    /// return would otherwise be dropped. Measured in production: after a 698 KB-segment movie stopped,
    /// its class still held 323 idle buffers (242 MB of a 512 MB cap) two minutes later, because the
    /// live 4.25 MB class had ~11 MB of headroom and so never had to reclaim anything. Retention stayed
    /// within the cap, but half of it was doing nothing. Nobody demanding the space is precisely when
    /// it should be given back rather than held.</para>
    /// </summary>
    public long TrimStale()
    {
        long trimmed = 0;
        foreach (var (_, state) in StaleClasses())
        {
            while (state.Idle.TryPop(out var buffer))
            {
                Interlocked.Add(ref _idleBytes, -buffer.Length);
                trimmed += buffer.Length;
            }
        }
        Interlocked.Add(ref _trimmedBytes, trimmed);
        return trimmed;
    }

    /// <summary>
    /// Runs <see cref="TrimStale"/> at most once per <see cref="TrimInterval"/>. Called from the rent
    /// path so no background timer is needed: the case that matters — one class dead while another is
    /// live — always has something renting. A process with no rents at all is idle anyway.
    /// </summary>
    private void MaybeTrimStale()
    {
        var now = _nowTicks();
        var last = Interlocked.Read(ref _lastTrimTicks);
        if (now - last < (long)(TrimInterval.TotalSeconds * Stopwatch.Frequency)) return;
        if (Interlocked.CompareExchange(ref _lastTrimTicks, now, last) != last) return; // another thread is on it
        TrimStale();
    }

    /// <summary>Classes idle for longer than <see cref="StaleAfter"/>, stalest first.</summary>
    private KeyValuePair<int, SizeClass>[] StaleClasses()
    {
        var staleBefore = _nowTicks() - (long)(StaleAfter.TotalSeconds * Stopwatch.Frequency);
        return _sizeClasses
            .Where(kvp => Volatile.Read(ref kvp.Value.LastRentTicks) < staleBefore)
            .OrderBy(kvp => Volatile.Read(ref kvp.Value.LastRentTicks))
            .ToArray();
    }

    /// <summary>Drops every idle buffer. Used by the GC diagnostics endpoint to prove retention is ours.</summary>
    public void Clear()
    {
        foreach (var state in _sizeClasses.Values)
        {
            while (state.Idle.TryPop(out var buffer))
            {
                Interlocked.Add(ref _idleBytes, -buffer.Length);
            }
        }
    }

    /// <summary>Per-size-class idle counts, for the diagnostics report.</summary>
    public IEnumerable<(int SizeClass, int IdleCount)> DescribeIdleBuffers()
    {
        foreach (var (sizeClass, state) in _sizeClasses)
        {
            var count = state.Idle.Count;
            if (count > 0) yield return (sizeClass, count);
        }
    }

    private static int RoundUpToSizeClass(int length) =>
        (int)(((long)length + Granularity - 1) / Granularity) * Granularity;

    private static long ReadMaxIdleBytesFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("NZBDAV_SEGMENT_POOL_MAX_IDLE_MB");
        if (long.TryParse(raw, out var megabytes) && megabytes >= 0)
            return megabytes * 1024 * 1024;
        return DefaultMaxIdleBytes;
    }
}
