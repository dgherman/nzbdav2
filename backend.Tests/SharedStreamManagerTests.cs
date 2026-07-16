using System;
using System.Threading;
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
}
