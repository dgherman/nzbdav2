using System;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Streams;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Regression coverage for a pre-warm starvation bug: PR #25 pre-warms the next RAR volume's stream
/// (via IWarmableStream.WarmupAsync) ahead of the boundary so a sequential crossing does not stall.
/// That pre-warm and any brand-new, unrelated stream request both draw from the same process-wide
/// concurrent-stream slot semaphore (BufferedSegmentStream.TryAcquireSlot). Before this fix, both
/// sides made exactly one non-blocking Wait(0) attempt, so whichever happened to call first won —
/// and production logs showed a fresh/unrelated request (e.g. a duplicate byte-0 fetch of the same
/// file) winning the last free slot moments before an already-playing stream's own pre-warm needed
/// one, degrading that pre-warm to the raw/unbuffered fallback right at the crossing.
///
/// The fix: a pre-warm-tagged acquisition (TryAcquireSlot(isPrewarm: true)) still tries the instant,
/// non-blocking path first, but on failure blocks (bounded by PrewarmSlotAcquireTimeout) for a slot
/// to free instead of giving up immediately. A non-pre-warm acquisition is completely unchanged:
/// still exactly one non-blocking Wait(0) attempt, so it degrades instantly under contention rather
/// than waiting. This is safe because isPrewarm=true is only ever reached from
/// CombinedStream.PrewarmPartAsync's own background Task.Run — never a request-serving thread — so
/// blocking there cannot stall an in-flight read.
///
/// [Collection(BufferedStreamCollection.Name)]: touches the same process-wide slot semaphore every
/// other test in that collection serializes against (see StreamChurnTests, CombinedStreamPrewarmTests).
/// </summary>
[Collection(BufferedStreamCollection.Name)]
public class BufferedSegmentStreamPrewarmSlotPriorityTests
{
    public BufferedSegmentStreamPrewarmSlotPriorityTests()
    {
        // Single-slot pool: the simplest reproduction of "the one remaining slot is contested".
        BufferedSegmentStream.SetMaxConcurrentStreams(1);
    }

    private static void Restore()
    {
        BufferedSegmentStream.SetMaxConcurrentStreams(32); // assembly-wide default other tests assume
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(5); // production default
    }

    [Fact]
    public async Task Prewarm_AcquiresSlot_OnceHolderReleases_InsteadOfFailingInstantly()
    {
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(5);
        try
        {
            // Something else (an already-active stream's own current volume, or a fresh/unrelated
            // request) holds the sole slot.
            var holder = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(holder);

            // The active stream's own pre-warm of its next RAR volume asks for a slot while none are
            // free. Before the fix this would return null immediately (a single Wait(0)); now it
            // should block waiting for one.
            var prewarmTask = Task.Run(() => BufferedSegmentStream.TryAcquireSlot(isPrewarm: true));

            // Give the background wait a moment to actually start blocking, rather than racing it.
            var finishedEarly = await Task.WhenAny(prewarmTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
            Assert.NotSame(prewarmTask, finishedEarly); // still waiting, not failed-fast

            // The holder's short-lived work finishes and it releases the slot.
            holder!.Release();

            var acquired = await prewarmTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(acquired);
            acquired!.Release();
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public async Task FreshRequest_DegradesInstantly_WhileActiveStreamsPrewarmIsStillWaiting()
    {
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(5);
        try
        {
            // The active stream's own current volume holds the sole slot.
            var activeStream = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(activeStream);

            // Its own pre-warm for the next volume starts waiting for a slot.
            var prewarmTask = Task.Run(() => BufferedSegmentStream.TryAcquireSlot(isPrewarm: true));
            await Task.Delay(TimeSpan.FromMilliseconds(200)); // let it actually start waiting

            // A brand-new, unrelated request (e.g. the rogue byte-0 fetch from production) arrives
            // concurrently. It must degrade (fail fast) rather than block, exactly as before this fix.
            var freshSw = System.Diagnostics.Stopwatch.StartNew();
            var freshResult = BufferedSegmentStream.TryAcquireSlot(isPrewarm: false);
            freshSw.Stop();
            Assert.Null(freshResult);
            Assert.True(freshSw.ElapsedMilliseconds < 1000,
                $"a non-pre-warm acquisition must fail instantly, not wait — took {freshSw.ElapsedMilliseconds}ms");

            // The fresh request already gave up (it never retries). Once the active stream's current
            // volume finishes and releases, its OWN pre-warm — which was already waiting — gets the
            // freed slot.
            activeStream!.Release();
            var prewarmResult = await prewarmTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(prewarmResult);
            prewarmResult!.Release();
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public async Task Prewarm_TimesOutAndDegrades_WhenNoSlotFreesInTime()
    {
        // Bounded, not indefinite: if nothing frees a slot in time, pre-warm must give up and let the
        // caller fall back — exactly like the pre-fix behavior, just after a bounded wait instead of
        // instantly.
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var holder = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(holder);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => BufferedSegmentStream.TryAcquireSlot(isPrewarm: true));
            sw.Stop();

            Assert.Null(result);
            Assert.True(sw.ElapsedMilliseconds >= 150, "should have actually waited close to the timeout");
            Assert.True(sw.ElapsedMilliseconds < 5000, "must not wait far past the configured timeout");

            holder!.Release();
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public async Task Prewarm_StopsWaiting_WhenCancelled()
    {
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(30);
        try
        {
            var holder = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(holder);

            using var cts = new CancellationTokenSource();
            var prewarmTask = Task.Run(() => BufferedSegmentStream.TryAcquireSlot(isPrewarm: true, ct: cts.Token));
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => prewarmTask.WaitAsync(TimeSpan.FromSeconds(5)));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2000, "cancellation should unblock the wait promptly, not sit through the full timeout");

            holder!.Release();
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void NonPrewarm_AcquireBehavior_IsUnchanged()
    {
        try
        {
            var first = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(first);

            // Sole slot taken — a second non-pre-warm attempt must fail instantly, same as before.
            var second = BufferedSegmentStream.TryAcquireSlot(isPrewarm: false);
            Assert.Null(second);

            first!.Release();

            var third = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(third);
            third!.Release();
        }
        finally
        {
            Restore();
        }
    }
}
