using NzbWebDAV.Utils;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// The arithmetic that sizes streaming memory from the process's own heap ceiling. Pure, so the
/// whole matrix of server specs can be checked without running a stream.
///
/// Context (2026-07-28): the shipped image caps the managed heap at 512 MB while the streaming
/// defaults were tuned against a NAS running a 4 GiB limit — a 256 MB per-stream prefetch floor, 8
/// concurrent streams each holding a 32 MB ring, and 512 MB of pool idle retention. One stream was
/// enough to exhaust the 512 MB heap and abort the process. These functions exist so the defaults
/// scale with the box instead of being constants that only suit one of them.
/// </summary>
public class MemoryBudgetTests
{
    private const long Mb = 1024 * 1024;

    [Theory]
    // heap,      streams, expected per-stream prefetch DATA bytes
    [InlineData(512 * Mb, 2, 48 * Mb)]     // shipped image: 20% of heap / 2 streams, rounded to 8 MB
    [InlineData(1024 * Mb, 4, 48 * Mb)]
    [InlineData(2048 * Mb, 4, 96 * Mb)]
    [InlineData(4096 * Mb, 8, 96 * Mb)]
    public void PerStreamPrefetchBytes_ScalesWithHeapAndConcurrency(long heap, int streams, long expected)
    {
        Assert.Equal(expected, MemoryBudget.PerStreamPrefetchBytes(heap, streams));
    }

    [Fact]
    public void PerStreamPrefetchBytes_NeverExceedsTheValidatedFloor()
    {
        // 256 MB is the production-validated window from v0.11.5. A bigger box buys more streams and
        // more headroom, not a bigger window per stream — the window is sized for refill latency, and
        // past this it is only holding memory.
        Assert.Equal(256 * Mb, MemoryBudget.PerStreamPrefetchBytes(64L * 1024 * Mb, 2));
    }

    [Fact]
    public void PerStreamPrefetchBytes_HasAFloorSoAStreamIsNeverStarved()
    {
        // PR #21 measured that too small a window starves the reader and trips circuit breakers. On a
        // box this small the honest answer is "few streams, small window", not "no window".
        Assert.Equal(16 * Mb, MemoryBudget.PerStreamPrefetchBytes(64 * Mb, 8));
    }

    [Theory]
    // A 32 MB ring per slot plus a full prefetch window per slot has to fit, so slots scale with heap.
    [InlineData(512 * Mb, 2)]
    [InlineData(1024 * Mb, 2)]
    [InlineData(2048 * Mb, 4)]
    [InlineData(4096 * Mb, 8)]
    [InlineData(16384 * Mb, 8)]
    public void MaxConcurrentStreams_ScalesWithHeap(long heap, int expected)
    {
        Assert.Equal(expected, MemoryBudget.MaxConcurrentStreams(heap));
    }

    [Fact]
    public void MaxConcurrentStreams_NeverDropsBelowTwo()
    {
        // One slot cannot serve a multipart playback: the player holds a part open while probing the
        // next one, so a single slot deadlocks rather than degrades.
        Assert.Equal(2, MemoryBudget.MaxConcurrentStreams(16 * Mb));
    }

    [Theory]
    [InlineData(512 * Mb, 76 * Mb)]      // 15% of heap
    [InlineData(4096 * Mb, 512 * Mb)]    // capped at the previous fixed default
    [InlineData(64 * Mb, 32 * Mb)]       // floored: below this the pool stops being a pool
    public void PoolIdleCapBytes_ScalesWithHeap(long heap, long expected)
    {
        Assert.Equal(expected, MemoryBudget.PoolIdleCapBytes(heap));
    }

    [Fact]
    public void TotalCommitment_FitsInsideTheHeapItWasDerivedFrom()
    {
        // The point of the whole exercise: for every box size, rings + resident prefetch + pool
        // retention must leave room for the rest of the process. Prefetch is counted at twice its
        // data size because each in-flight segment is held twice — once in the pooled buffer, once
        // in the yEnc pipe's own copy (measured: 255 MB of data drove a 490 MB heap).
        foreach (var heap in new[] { 256 * Mb, 512 * Mb, 1024 * Mb, 2048 * Mb, 4096 * Mb, 16384 * Mb })
        {
            var streams = MemoryBudget.MaxConcurrentStreams(heap);
            var perStream = MemoryBudget.PerStreamPrefetchBytes(heap, streams);
            var committed = streams * (perStream * MemoryBudget.ResidentBytesPerDataByte + MemoryBudget.RingBufferBytes)
                            + MemoryBudget.PoolIdleCapBytes(heap);

            Assert.True(committed <= heap * 0.85,
                $"heap={heap / Mb}MB: commitment {committed / Mb}MB exceeds 85% of it " +
                $"({streams} streams x {perStream / Mb}MB data)");
        }
    }

    [Fact]
    public void DetectedHeapLimit_IsPositive()
    {
        // Whatever the runtime reports, the budget must never be zero or negative — every derived
        // value would collapse to its floor and the sizing would silently stop tracking the box.
        Assert.True(MemoryBudget.HeapLimitBytes > 0);
    }

    /// <summary>
    /// Per-volume cost of parsing RAR headers unbuffered: two articles in flight, each resident
    /// twice. Mirrors what RarProcessor computes.
    /// </summary>
    private static long RarHeaderBytesPerPart(long segmentBytes) =>
        segmentBytes * 2 * MemoryBudget.ResidentBytesPerDataByte;

    [Theory]
    [InlineData(512, 750 * 1024)]
    [InlineData(1024, 750 * 1024)]
    [InlineData(4096, 750 * 1024)]
    public void MaxConcurrentRarHeaderParts_BeatsTheOldFixedBudgetOnTypicalSegments(
        long heapMb, long segmentBytes)
    {
        // The fixed budget this replaced allowed three volumes at a time regardless of the box,
        // which cost ~240s on a 71-volume archive. On any supported heap with ordinary segment
        // sizes the derived value must be a real improvement, not a rename of the same number.
        var parts = MemoryBudget.MaxConcurrentRarHeaderParts(
            heapMb * Mb, RarHeaderBytesPerPart(segmentBytes));

        Assert.True(parts > 3, $"heap={heapMb}MB gave only {parts} concurrent volumes");
    }

    [Fact]
    public void MaxConcurrentRarHeaderParts_ScalesWithHeap()
    {
        var small = MemoryBudget.MaxConcurrentRarHeaderParts(512 * Mb, RarHeaderBytesPerPart(750 * 1024));
        var large = MemoryBudget.MaxConcurrentRarHeaderParts(4096 * Mb, RarHeaderBytesPerPart(750 * 1024));

        Assert.True(large > small, $"4GB heap allowed {large}, 512MB allowed {small}");
    }

    [Fact]
    public void MaxConcurrentRarHeaderParts_ShrinksAsSegmentsGrow()
    {
        // A release posted with 4 MB segments costs several times a 750 KB one for the same
        // parallelism, which is the whole reason this takes bytes rather than a volume count.
        var typical = MemoryBudget.MaxConcurrentRarHeaderParts(
            512 * Mb, RarHeaderBytesPerPart(750 * 1024));
        var large = MemoryBudget.MaxConcurrentRarHeaderParts(
            512 * Mb, RarHeaderBytesPerPart(4 * Mb));

        Assert.True(large < typical, $"4MB segments allowed {large}, 750KB allowed {typical}");
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(512)]
    public void MaxConcurrentRarHeaderParts_NeverDropsBelowTheOldFloor(long heapMb)
    {
        // Even a tiny heap with huge segments must not import more slowly than before the change.
        Assert.True(
            MemoryBudget.MaxConcurrentRarHeaderParts(heapMb * Mb, RarHeaderBytesPerPart(16 * Mb)) >= 3);
    }

    [Fact]
    public void MaxConcurrentRarHeaderParts_SurvivesUnusableInputs()
    {
        Assert.True(MemoryBudget.MaxConcurrentRarHeaderParts(0, RarHeaderBytesPerPart(750 * 1024)) >= 3);
        Assert.True(MemoryBudget.MaxConcurrentRarHeaderParts(512 * Mb, 0) >= 3);
        Assert.True(MemoryBudget.MaxConcurrentRarHeaderParts(-1, -1) >= 3);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void RarHeaderParts_StayWithinTheirShareOfTheHeap(long heapMb)
    {
        // The floor can exceed the share on a small heap; above it the derived count must respect
        // the budget it was derived from.
        var bytesPerPart = RarHeaderBytesPerPart(750 * 1024);
        var parts = MemoryBudget.MaxConcurrentRarHeaderParts(heapMb * Mb, bytesPerPart);

        Assert.True(parts * bytesPerPart <= heapMb * Mb * 0.15,
            $"heap={heapMb}MB: {parts} volumes x {bytesPerPart / 1024}KB exceeds its share");
    }
}
