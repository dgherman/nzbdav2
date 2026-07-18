using System.Collections.Concurrent;

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

    private readonly int _maxBufferSize;
    private readonly long _maxIdleBytes;
    private readonly ConcurrentDictionary<int, ConcurrentStack<byte[]>> _sizeClasses = new();
    private long _idleBytes;

    /// <summary>
    /// The process-wide pool used by <see cref="BufferedSegmentStream"/>. The idle cap is overridable
    /// via <c>NZBDAV_SEGMENT_POOL_MAX_IDLE_MB</c> so it can be tuned against
    /// <c>POST /api/gc-diagnostics</c> without a rebuild.
    /// </summary>
    public static readonly SegmentBufferPool Shared = new(DefaultMaxBufferSize, ReadMaxIdleBytesFromEnvironment());

    public SegmentBufferPool(int maxBufferSize, long maxIdleBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferSize, Granularity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxIdleBytes);
        _maxBufferSize = maxBufferSize;
        _maxIdleBytes = maxIdleBytes;
    }

    /// <summary>Bytes currently held in the pool but not checked out. Bounded by the idle cap.</summary>
    public long IdleBytes => Interlocked.Read(ref _idleBytes);

    /// <summary>The absolute ceiling on <see cref="IdleBytes"/>.</summary>
    public long MaxIdleBytes => _maxIdleBytes;

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
        if (_sizeClasses.TryGetValue(sizeClass, out var stack) && stack.TryPop(out var pooled))
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
        // collectively overshoot the cap.
        while (true)
        {
            var current = Interlocked.Read(ref _idleBytes);
            if (current + length > _maxIdleBytes) return; // At capacity — drop it.
            if (Interlocked.CompareExchange(ref _idleBytes, current + length, current) == current) break;
        }

        _sizeClasses.GetOrAdd(length, static _ => new ConcurrentStack<byte[]>()).Push(buffer);
    }

    /// <summary>Drops every idle buffer. Used by the GC diagnostics endpoint to prove retention is ours.</summary>
    public void Clear()
    {
        foreach (var stack in _sizeClasses.Values)
        {
            while (stack.TryPop(out var buffer))
            {
                Interlocked.Add(ref _idleBytes, -buffer.Length);
            }
        }
    }

    /// <summary>Per-size-class idle counts, for the diagnostics report.</summary>
    public IEnumerable<(int SizeClass, int IdleCount)> DescribeIdleBuffers()
    {
        foreach (var (sizeClass, stack) in _sizeClasses)
        {
            var count = stack.Count;
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
