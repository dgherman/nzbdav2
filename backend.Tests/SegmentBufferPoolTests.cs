using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests;

/// <summary>
/// Tests for the dedicated segment buffer pool (issue #23).
///
/// The two properties that matter, and that <see cref="System.Buffers.ArrayPool{T}"/> —
/// both Shared and Create() — fails to provide:
///   1. Rents are right-sized. ArrayPool rounds every rent up to a 16&lt;&lt;i bucket, so a
///      4.19 MB segment occupies an 8 MB array (3.8 MB wasted per in-flight buffer).
///   2. Idle retention is bounded in BYTES. ArrayPool.Create bounds arrays-per-bucket, a
///      count — across 20 doubling buckets that permits gigabytes.
/// </summary>
public class SegmentBufferPoolTests
{
    private const int Kb = 1024;
    private const int Mb = 1024 * 1024;

    [Fact]
    public void Rent_RoundsUpToGranularity_NotToPowerOfTwo()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 64 * Mb);

        // The case from issue #23: a 4.19 MB segment must not land in an 8 MB array.
        var buffer = pool.Rent(4 * Mb + 4096);

        Assert.True(buffer.Length >= 4 * Mb + 4096, "buffer must satisfy the request");
        Assert.True(buffer.Length < 8 * Mb, $"expected right-sized buffer, got {buffer.Length} (power-of-two rounding)");
        Assert.Equal(0, buffer.Length % SegmentBufferPool.Granularity);
    }

    [Theory]
    [InlineData(717 * Kb)]
    [InlineData(1 * Mb)]
    [InlineData(1 * Mb + 1)]
    [InlineData(4 * Mb + 4096)]
    [InlineData(8 * Mb)]
    public void Rent_NeverWastesMoreThanOneGranule(int requested)
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 64 * Mb);

        var buffer = pool.Rent(requested);

        Assert.True(buffer.Length >= requested);
        Assert.True(buffer.Length - requested < SegmentBufferPool.Granularity,
            $"waste {buffer.Length - requested} exceeds one granule for a {requested} byte request");
    }

    [Fact]
    public void Rent_AfterReturn_ReusesTheSameArray()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 64 * Mb);

        var first = pool.Rent(2 * Mb);
        pool.Return(first);
        var second = pool.Rent(2 * Mb);

        Assert.Same(first, second);
    }

    [Fact]
    public void Rent_SizeClassesAreIndependent()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 64 * Mb);

        var small = pool.Rent(1 * Mb);
        pool.Return(small);

        // A larger request must not be served the smaller array.
        var large = pool.Rent(4 * Mb);
        Assert.NotSame(small, large);
        Assert.True(large.Length >= 4 * Mb);
    }

    [Fact]
    public void Return_StopsRetainingOnceIdleByteCapIsReached()
    {
        // Cap admits exactly two 4 MB buffers.
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 8 * Mb);

        var a = pool.Rent(4 * Mb);
        var b = pool.Rent(4 * Mb);
        var c = pool.Rent(4 * Mb);
        Assert.Equal(0, pool.IdleBytes);

        pool.Return(a);
        pool.Return(b);
        Assert.Equal(8 * Mb, pool.IdleBytes);

        // Third return exceeds the cap and must be dropped for the GC, not retained.
        pool.Return(c);
        Assert.Equal(8 * Mb, pool.IdleBytes);
    }

    [Fact]
    public void IdleBytes_NeverExceedsCap_UnderMixedSizes()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 16 * Mb);

        var rented = new List<byte[]>();
        foreach (var size in new[] { 1 * Mb, 2 * Mb, 4 * Mb, 8 * Mb, 1 * Mb, 2 * Mb, 4 * Mb, 8 * Mb })
            rented.Add(pool.Rent(size));
        foreach (var buffer in rented)
            pool.Return(buffer);

        Assert.True(pool.IdleBytes <= 16 * Mb, $"idle retention {pool.IdleBytes} exceeded the {16 * Mb} cap");
    }

    [Fact]
    public void Rent_AboveMaxBufferSize_AllocatesUnpooled()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 4 * Mb, maxIdleBytes: 64 * Mb);

        var oversize = pool.Rent(8 * Mb);
        Assert.True(oversize.Length >= 8 * Mb);

        // Returning an oversize array must not add it to the idle pool.
        pool.Return(oversize);
        Assert.Equal(0, pool.IdleBytes);
    }

    [Fact]
    public void Return_IgnoresForeignArraysThatDoNotMatchASizeClass()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 64 * Mb);

        // Not a multiple of the granularity, so it was never handed out by this pool.
        pool.Return(new byte[SegmentBufferPool.Granularity + 1]);

        Assert.Equal(0, pool.IdleBytes);
    }

    [Fact]
    public void Return_NullIsIgnored()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 64 * Mb);

        pool.Return(null!);

        Assert.Equal(0, pool.IdleBytes);
    }

    [Fact]
    public async Task RentAndReturn_AreThreadSafe()
    {
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 32 * Mb);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var buffer = pool.Rent(1 * Mb + (i % 4) * 512 * Kb);
                buffer[0] = 1;
                pool.Return(buffer);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.True(pool.IdleBytes >= 0);
        Assert.True(pool.IdleBytes <= 32 * Mb, $"idle retention {pool.IdleBytes} exceeded the cap under concurrency");
    }

    [Fact]
    public void RetainedBytes_AreBoundedFarBelowArrayPoolEquivalent()
    {
        // Regression guard for the finding that motivated this class: ArrayPool.Create's
        // per-bucket *count* bound permits ~2.5 GB across its buckets. A byte cap is absolute.
        var pool = new SegmentBufferPool(maxBufferSize: 16 * Mb, maxIdleBytes: 256 * Mb);

        var rented = new List<byte[]>();
        for (var i = 0; i < 200; i++)
            rented.Add(pool.Rent(4 * Mb + 4096));
        foreach (var buffer in rented)
            pool.Return(buffer);

        Assert.True(pool.IdleBytes <= 256 * Mb);
    }
}
