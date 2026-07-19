using System;
using System.Threading;
using NzbWebDAV.Metrics;
using NzbWebDAV.Streams;
using Xunit;

namespace NzbWebDAV.Tests;

public class SharedStreamManagerTests
{
    private const int RingBufferSize = 64 * 1024;
    private const long StreamLength = 100_000_000;

    [Fact]
    public void GetOrCreate_WhenEntryExistsButPositionCannotAttach_DoesNotRunTheFactoryAgain()
    {
        // Regression (click-to-play): a player opens an .mkv, then immediately seeks to the tail to read
        // the Matroska Cues. That position is far ahead of the front pump, so attaching is correctly
        // refused — but the manager then built a whole entry anyway (starting a real fetch pipeline:
        // permits, connections, segment reads) only to lose the TryAdd against the entry it had already
        // found, and tear it down synchronously. Every single file open paid for a wasted pipeline.
        var davItemId = Guid.NewGuid();
        var factoryCalls = 0;

        SharedStreamManager.SharedStreamFactoryResult Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCalls);
            // Gate the first read so the pump never advances: WritePosition stays at 0, which makes the
            // tail attach below deterministically out of range.
            return new SharedStreamManager.SharedStreamFactoryResult(
                new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true));
        }

        var front = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 0, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);

        Assert.NotNull(front);
        Assert.Equal(1, factoryCalls);

        // The tail/Cues probe, hundreds of MB ahead of the write frontier.
        var tail = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 90_000_000, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);

        Assert.Null(tail); // caller falls back to a private stream at the seek target
        Assert.Equal(1, factoryCalls); // pre-fix: 2

        front!.Dispose(); // last reader leaves -> 0s grace -> entry evicts and releases its slot
        SharedStreamManager.Evict(davItemId);
    }

    [Fact]
    public void GetOrCreate_WhenNoEntryExists_CreatesOnceAndAttachesSubsequentReaderAtSamePosition()
    {
        var davItemId = Guid.NewGuid();
        var factoryCalls = 0;

        SharedStreamManager.SharedStreamFactoryResult Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCalls);
            return new SharedStreamManager.SharedStreamFactoryResult(
                new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true));
        }

        var first = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 0, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);
        Assert.NotNull(first);

        // A second request for the same file at the same position must share, not rebuild.
        var second = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 0, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);

        Assert.NotNull(second);
        Assert.Equal(1, factoryCalls);

        second!.Dispose();
        first!.Dispose();
        SharedStreamManager.Evict(davItemId);
    }

    [Fact]
    public void AttachOutcomes_AreCountedExactlyOncePerAttempt()
    {
        // Regression (issue #18): NzbFileStream calls TryAttach and, on null, falls through to
        // GetOrCreate. Both used to increment a miss counter for the SAME request — once as
        // "position_out_of_range" and once as "existing_entry_unattachable" — so a single failure
        // mode was reported as two independent ones and the miss total was double the real number
        // of attach attempts. The paired counts in the issue's production data are the fingerprint.
        var davItemId = Guid.NewGuid();

        SharedStreamManager.SharedStreamFactoryResult Factory(CancellationToken ct) =>
            new(new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true));

        var hitsBefore = ReadCounter(AppMetrics.SharedStreamHits);
        var missesBefore = TotalMisses();

        var front = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 0, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);
        Assert.NotNull(front);

        // One request for the tail, taking the real call path: TryAttach first, then GetOrCreate.
        Assert.Null(SharedStreamManager.TryAttach(davItemId, 90_000_000));
        Assert.Null(SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 90_000_000, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory));

        // One attach attempt -> exactly one miss, zero hits. Pre-fix the miss count was 2.
        Assert.Equal(1, TotalMisses() - missesBefore);
        Assert.Equal(0, ReadCounter(AppMetrics.SharedStreamHits) - hitsBefore);

        // And the mirror case: a reader at the pump's own position attaches on the TryAttach call,
        // never reaches GetOrCreate, and books exactly one hit and no miss.
        var second = SharedStreamManager.TryAttach(davItemId, 0);
        Assert.NotNull(second);

        Assert.Equal(1, TotalMisses() - missesBefore);
        Assert.Equal(1, ReadCounter(AppMetrics.SharedStreamHits) - hitsBefore);

        second!.Dispose();
        front!.Dispose();
        SharedStreamManager.Evict(davItemId);
    }

    [Fact]
    public void TailSeek_IsReportedAsAheadOfFrontier_WithTheDistanceThatDecidesTheFix()
    {
        // The distance is the whole point of the instrumentation: a rejection 8 MB past the frontier
        // would be recovered by a larger ring, one 90 MB past it (here, against a 64 KB ring) would
        // not. Without the reason and the distance, both look identical in the metrics.
        var slot = new SemaphoreSlim(1, 1);
        Assert.True(slot.Wait(0)); // the entry owns the slot and releases it during cleanup

        var entry = new SharedStreamEntry(
            new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true),
            slot,
            Guid.NewGuid(),
            basePosition: 0,
            streamLength: StreamLength,
            ringBufferSize: RingBufferSize,
            gracePeriodSeconds: 0,
            evictCallback: _ => { },
            entryCts: new CancellationTokenSource());

        var handle = entry.TryAttachReader(90_000_000, out var rejection, out var distance);

        Assert.Null(handle);
        Assert.Equal(SharedStreamEntry.AttachRejection.AheadOfFrontier, rejection);
        Assert.Equal(90_000_000, distance); // frontier is still 0; positive means "past the pump"

        entry.Dispose();
    }

    private static double ReadCounter(Prometheus.Counter counter) => counter.Value;

    private static double TotalMisses()
    {
        // Sum across every reason label so the assertion holds no matter which reason a miss is
        // filed under — the invariant under test is the count, not the labelling.
        double total = 0;
        foreach (var labels in MissReasons)
            total += AppMetrics.SharedStreamMisses.WithLabels(labels).Value;
        return total;
    }

    private static readonly string[] MissReasons =
    {
        "no_entry", "entry_unusable", "before_base", "behind_window", "ahead_of_frontier",
        "past_end", "unknown",
    };
}
