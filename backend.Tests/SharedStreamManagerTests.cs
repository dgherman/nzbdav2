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

    static SharedStreamManagerTests()
    {
        // The process default is 2 concurrent stream slots (production config raises it to 8).
        // Entries hold a slot until their grace timer fires and cleanup runs, which is asynchronous,
        // so with only 2 slots these tests starve each other and whichever runs second sees
        // GetOrCreate return null for reasons unrelated to what it is testing. Give the assembly a
        // budget large enough that slot supply is never the variable under test.
        BufferedSegmentStream.SetMaxConcurrentStreams(32);
    }

    [Fact]
    public void GetOrCreate_WhenNoEntryCoversThePosition_CreatesASecondRegionEntry()
    {
        // This replaces GetOrCreate_WhenEntryExistsButPositionCannotAttach_DoesNotRunTheFactoryAgain,
        // whose expectation was the opposite: it asserted the tail read got NO entry and fell back to
        // a private stream. That guard existed to stop a wasted fetch pipeline on click-to-play, but
        // production measurement (issue #18, v0.11.10) showed what it actually cost: the only entry a
        // file ever had was anchored at byte 0, every subsequent reader was a mean 3.85 GB downstream,
        // and all of them took full private BufferedSegmentStreams instead. Two movies, 11 streams.
        //
        // The fallback was never the cheap option — this path is only reached for open-ended requests,
        // so the private stream carries its own multi-hundred-MB prefetch window. A second entry costs
        // the same inner stream plus one ring, and can be shared.
        var davItemId = Guid.NewGuid();
        var factoryCalls = 0;

        SharedStreamManager.SharedStreamFactoryResult Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCalls);
            // Gate the first read so the pump never advances: WritePosition stays at 0, which makes the
            // tail attach below deterministically out of range of the front entry.
            return new SharedStreamManager.SharedStreamFactoryResult(
                new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true));
        }

        var front = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 0, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);

        Assert.NotNull(front);
        Assert.Equal(1, factoryCalls);

        // The tail/Cues read, far ahead of the front pump: its own region, its own entry.
        var tail = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 90_000_000, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);

        Assert.NotNull(tail);
        Assert.Equal(2, factoryCalls);
        Assert.Equal(2, SharedStreamManager.CountEntries(davItemId));

        // And the point of doing so: a second reader at the tail now SHARES that entry rather than
        // building yet another private stream. Under the old guard this was a miss.
        var tailFollower = SharedStreamManager.TryAttach(davItemId, 90_000_000);
        Assert.NotNull(tailFollower);
        Assert.Equal(2, factoryCalls);

        tailFollower!.Dispose();
        tail!.Dispose();
        front!.Dispose();
        SharedStreamManager.Evict(davItemId);
    }

    [Fact]
    public void GetOrCreate_StopsCreatingEntriesAtTheCap()
    {
        // Each entry costs a ring buffer plus one of the scarce global stream slots, so one file must
        // not be able to take all of them. Past the cap the caller falls back to a private stream,
        // which is the pre-v0.11.11 behaviour for every read past the first.
        var original = SharedStreamManager.MaxEntriesPerFile;
        SharedStreamManager.MaxEntriesPerFile = 2;
        var davItemId = Guid.NewGuid();
        var factoryCalls = 0;

        try
        {
            SharedStreamManager.SharedStreamFactoryResult Factory(CancellationToken ct)
            {
                Interlocked.Increment(ref factoryCalls);
                return new SharedStreamManager.SharedStreamFactoryResult(
                    new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true));
            }

            var first = SharedStreamManager.GetOrCreate(
                davItemId, startPosition: 0, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);
            var second = SharedStreamManager.GetOrCreate(
                davItemId, startPosition: 50_000_000, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(2, factoryCalls);

            // Third distinct region: refused, and the factory must NOT run — no pipeline is built
            // only to be thrown away.
            var third = SharedStreamManager.GetOrCreate(
                davItemId, startPosition: 90_000_000, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);

            Assert.Null(third);
            Assert.Equal(2, factoryCalls);
            Assert.Equal(2, SharedStreamManager.CountEntries(davItemId));

            second!.Dispose();
            first!.Dispose();
        }
        finally
        {
            SharedStreamManager.MaxEntriesPerFile = original;
            SharedStreamManager.Evict(davItemId);
        }
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
        // TryAttach misses; GetOrCreate now serves it by opening a second region entry — and books
        // that on the create counter, NOT as a hit, because this request's attach outcome is already
        // recorded.
        Assert.Null(SharedStreamManager.TryAttach(davItemId, 90_000_000));
        var tail = SharedStreamManager.GetOrCreate(
            davItemId, startPosition: 90_000_000, StreamLength, RingBufferSize, gracePeriodSeconds: 0, Factory);
        Assert.NotNull(tail);

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
        tail!.Dispose();
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
            evictCallback: (_, _) => { },
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
