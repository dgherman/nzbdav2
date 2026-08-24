using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using Usenet.Nzb;
using UsenetSharp.Models;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Regression coverage for two round-2 cross-review findings on the pre-warm slot-priority fix
/// (BufferedSegmentStreamPrewarmSlotPriorityTests): NzbFileStream.EnsureInnerStreamAsync memoizes its
/// construction task onto _innerStreamTask so callers on the same instance share one construction per
/// generation.
///
/// Finding 1 (stall): a pre-warm-tagged construction (WarmupAsync -> isPrewarm: true) can be mid-flight
/// on TryAcquireSlot's bounded slot wait (up to PrewarmSlotAcquireTimeout) when a real read for the
/// SAME instance arrives — e.g. the actual boundary crossing the pre-warm was preparing for. Before
/// this fix, wrapping the shared await in Task.WaitAsync(ct) fixed CANCELLATION observability for that
/// live read, but an UNCANCELLED live read still inherited pre-warm's up-to-5s wait as its own latency
/// — a stall that never existed before pre-warming (a live read's own slot acquisition was always a
/// single non-blocking attempt). Fixed: a live caller that finds the memoized task is pre-warm-tagged
/// AND still pending supersedes it with its own fresh, live-tagged construction instead of joining it.
/// The original pre-warm task is not cancelled — it keeps running independently, and the existing
/// "adopted" check disposes its result instead of resurrecting it once superseded.
///
/// Finding 2 (wrong distinguishing signal): the original fix decided "is this MY OWN WaitAsync
/// cancelling, or did the construction itself fail?" by comparing the caught exception's
/// CancellationToken against the caller's ct — but two different callers can legitimately pass
/// equal-valued tokens (e.g. both CancellationToken.None), so token-value equality cannot reliably
/// tell those cases apart. Fixed: check the shared task's own terminal state (IsCanceled/IsFaulted)
/// instead — only a task that has actually failed/cancelled gets its memoization cleared.
/// </summary>
[Collection(BufferedStreamCollection.Name)]
public class NzbFileStreamPrewarmCoalescingCancellationTests
{
    private const int SegmentSize = 4096;
    private const int SegmentCount = 8;

    private sealed class InstantNntpClient : INntpClient
    {
        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct)
            => Task.FromResult(BuildHeader(segmentId));

        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
            => Task.FromResult(BuildStream(segmentId));

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>Header lookups (used by SeekSegment to resolve a non-zero Seek to a segment index)
    /// block until Release() is called, so a test can hold a construction "in flight" under full
    /// control. Each call independently honors its OWN cancellation token via Task.WhenAny — one
    /// caller's cancellation never cancels the shared gate for any other caller awaiting it.</summary>
    private sealed class GatedHeaderNntpClient : INntpClient
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct)
        {
            var delay = Task.Delay(Timeout.Infinite, ct);
            var completed = await Task.WhenAny(_gate.Task, delay).ConfigureAwait(false);
            if (completed != _gate.Task) ct.ThrowIfCancellationRequested();
            return BuildHeader(segmentId);
        }

        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
            => Task.FromResult(BuildStream(segmentId));

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static int ParseIndex(string segmentId) => int.Parse(segmentId.Split('-')[1].Split('@')[0]);

    private static UsenetYencHeader BuildHeader(string segmentId)
    {
        var index = ParseIndex(segmentId);
        return new UsenetYencHeader
        {
            FileName = "test.mkv",
            FileSize = (long)SegmentSize * SegmentCount,
            LineLength = 128,
            PartNumber = index + 1,
            TotalParts = SegmentCount,
            PartSize = SegmentSize,
            PartOffset = (long)SegmentSize * index,
        };
    }

    private static YencHeaderStream BuildStream(string segmentId)
    {
        var header = BuildHeader(segmentId);
        var index = ParseIndex(segmentId);
        var payload = new byte[SegmentSize];
        Array.Fill(payload, (byte)(index + 1));
        return new YencHeaderStream(header, null, new MemoryStream(payload));
    }

    private static NzbFileStream MakeStream(INntpClient client)
    {
        var ids = Enumerable.Range(0, SegmentCount).Select(i => $"seg-{i}@test").ToArray();
        // No DavItemId: skips the shared-stream path entirely, so TryAcquireSlot(isPrewarm, ct) is
        // called directly from GetCombinedStream's direct-path branch — the simplest way to put a
        // real slot wait on the critical path the first test below exercises.
        var context = new ConnectionUsageContext(
            ConnectionUsageType.Streaming, new ConnectionUsageDetails { Text = "coalescing-cancellation-test" });
        return new NzbFileStream(
            ids, fileSize: (long)SegmentSize * SegmentCount, client,
            concurrentConnections: 3, usageContext: context);
    }

    [Fact]
    public async Task LiveRead_ArrivingWhilePrewarmPending_DoesNotWaitForPrewarm_GetsOwnFastFallback()
    {
        BufferedSegmentStream.SetMaxConcurrentStreams(1);
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(10);
        try
        {
            // Something else holds the sole slot, so the pre-warm below has to actually wait.
            var holder = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(holder);

            var client = new InstantNntpClient();
            await using var stream = MakeStream(client);

            // Kick off a pre-warm exactly as CombinedStream.PrewarmPartAsync does: fire-and-forget,
            // its own token, no real read has touched this stream yet. This memoizes _innerStreamTask
            // as a pre-warm-tagged construction that is now blocked in TryAcquireSlot's wait.
            using var prewarmCts = new CancellationTokenSource();
            var warmupTask = Task.Run(() => stream.WarmupAsync(prewarmCts.Token));
            await Task.Delay(TimeSpan.FromMilliseconds(300)); // let it actually start waiting on the slot

            // A real read for the SAME instance arrives now (the boundary crossing this pre-warm was
            // preparing for). It must NOT wait for the pre-warm's up-to-10s slot wait or for
            // holder.Release() below — it should supersede with its own instant-fail fallback attempt.
            var buffer = new byte[SegmentSize];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"a live read must not inherit a pending pre-warm's slot wait — took {sw.ElapsedMilliseconds}ms");
            Assert.Equal(SegmentSize, read);
            Assert.Equal((byte)1, buffer[0]); // segment index 0's fill byte — real data, not a stall artifact

            // The superseded pre-warm construction must still resolve cleanly (not fault/hang) once
            // the slot frees, even though its result now gets disposed rather than adopted.
            holder!.Release();
            await warmupTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            BufferedSegmentStream.SetMaxConcurrentStreams(32);
            BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(5);
        }
    }

    [Fact]
    public async Task UnrelatedCallersOwnTokenCancelled_DoesNotPoisonSharedTask_OtherCallerStillSucceeds()
    {
        var client = new GatedHeaderNntpClient();
        await using var stream = MakeStream(client);

        // Move off position 0 so construction resolves through SeekSegment -> GetSegmentYencHeaderAsync
        // (position 0 takes GetFileStream's rangeStart==0 branch straight into GetCombinedStream and
        // never calls the client — see NzbFileStreamConstructionRetryTests).
        stream.Seek(SegmentSize * 2L, SeekOrigin.Begin);

        // This call CREATES _innerStreamTask, using an uncancelled token — the real (gated)
        // construction is driven entirely by CancellationToken.None here, independent of anything
        // the second caller below does.
        var buffer1 = new byte[SegmentSize];
        var creatorReadTask = stream.ReadAsync(buffer1, 0, buffer1.Length, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // let it start the gated header call

        // A second, unrelated caller coalesces onto the SAME in-flight task with its own independent
        // token and cancels ONLY that token.
        using var otherCts = new CancellationTokenSource();
        var buffer2 = new byte[SegmentSize];
        var otherReadTask = stream.ReadAsync(buffer2, 0, buffer2.Length, otherCts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        otherCts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => otherReadTask.WaitAsync(TimeSpan.FromSeconds(5)));
        sw.Stop();
        Assert.Equal(otherCts.Token, ex.CancellationToken);
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"the unrelated caller's own cancellation should unblock it promptly, took {sw.ElapsedMilliseconds}ms");

        // The shared construction itself must be untouched by the other caller's cancellation:
        // releasing the gate lets the ORIGINAL, uncancelled creator still get a real result.
        client.Release();
        var read = await creatorReadTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SegmentSize, read);
        Assert.Equal((byte)3, buffer1[0]); // segment index 2's fill byte — real data
    }

    /// <summary>Header lookup throws a real (non-cancellation) IOException on the first call, succeeds
    /// on every call after. Used so a construction's failure can be observed deterministically: with
    /// CancellationToken.None driving the caller's own Task.WaitAsync wrapper, that wrapper has nothing
    /// to independently short-circuit on, so it can ONLY complete once the underlying construction task
    /// itself has genuinely, fully transitioned to Faulted — unlike a cancellation-driven failure, there
    /// is no ambiguity between "this caller's own wait gave up" and "the construction really failed".</summary>
    private sealed class ThrowsOnceHeaderNntpClient : INntpClient
    {
        private int _calls;

        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) throw new IOException("simulated construction failure");
            return Task.FromResult(BuildHeader(segmentId));
        }

        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
            => Task.FromResult(BuildStream(segmentId));

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>
    /// Direct regression test for the round-4 finding: the ContinueWith that clears a terminally-failed
    /// _innerStreamTask runs on the thread-pool scheduler, not synchronously with the antecedent's own
    /// completion — so a caller whose gate check lands between "task went terminal" and "the
    /// continuation actually cleared the field" could otherwise adopt (and immediately re-fail with) an
    /// already-dead task. EnsureInnerStreamAsync now also checks the memoized task's Status inline,
    /// synchronously, inside the SAME lock where it's read — so recovery does not depend on the
    /// continuation's scheduling at all.
    ///
    /// This test makes that deterministic rather than probabilistic: it uses CancellationToken.None
    /// throughout (see ThrowsOnceHeaderNntpClient) so awaiting the failing read to completion is
    /// GUARANTEED — by construction, not by timing luck — to happen only once the underlying task has
    /// genuinely, fully transitioned to Faulted. It then asserts a single, single-attempt fresh read
    /// succeeds — no polling/retry loop, because the inline check makes this synchronous regardless of
    /// whether the ContinueWith backstop has run yet.
    /// </summary>
    [Fact]
    public async Task ConstructionFailure_ClearsMemoizedTaskSynchronously_NextCallerGetsFreshAttemptOnFirstTry()
    {
        var client = new ThrowsOnceHeaderNntpClient();
        await using var stream = MakeStream(client);
        stream.Seek(SegmentSize * 2L, SeekOrigin.Begin);

        var buffer1 = new byte[SegmentSize];
        await Assert.ThrowsAsync<IOException>(
            () => stream.ReadAsync(buffer1, 0, buffer1.Length, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        // Single attempt, no retry: the inline Status check in EnsureInnerStreamAsync must recognize
        // the dead task and start a fresh construction synchronously, regardless of whether the
        // ContinueWith backstop has run yet.
        var buffer2 = new byte[SegmentSize];
        var read = await stream.ReadAsync(buffer2, 0, buffer2.Length, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SegmentSize, read);
        Assert.Equal((byte)3, buffer2[0]); // segment index 2's fill byte — real retried data, not stale
    }

    /// <summary>
    /// Direct regression test for the round-3 finding: a per-caller snapshot check
    /// (`!task.IsCanceled &amp;&amp; !task.IsFaulted` read inside a catch block) races the construction
    /// task's own completion — a caller's Task.WaitAsync(ct) can throw a moment before the antecedent's
    /// status has actually settled, so the snapshot can see "not failed yet" for a construction that IS
    /// about to fail, leaving it memoized. The fix moved cleanup onto a ContinueWith attached at the
    /// construction task's creation, driven purely by that task's own terminal transition.
    ///
    /// This fires both an UNRELATED caller's own cancellation (must never clear the shared task) and
    /// the construction's OWN driving cancellation (must always clear it) as close together in time as
    /// possible, repeated several iterations, and then requires the next read to succeed on a single,
    /// short-timeout attempt — no bounded retry loop tolerating a race window, since the fix makes
    /// clearing deterministic rather than eventually-consistent.
    /// </summary>
    [Fact]
    public async Task ConstructionFailure_ClearsExactlyOnce_EvenUnderAdversarialConcurrentCancellation()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var client = new GatedHeaderNntpClient();
            await using var stream = MakeStream(client);
            stream.Seek(SegmentSize * 2L, SeekOrigin.Begin);

            // Creates _innerStreamTask; its own token drives the real, underlying construction, so
            // cancelling it genuinely fails the shared task itself.
            using var creatorCts = new CancellationTokenSource();
            var buffer1 = new byte[SegmentSize];
            var creatorReadTask = stream.ReadAsync(buffer1, 0, buffer1.Length, creatorCts.Token);

            // Coalesces onto the same in-flight task with its own, independent token.
            using var otherCts = new CancellationTokenSource();
            var buffer2 = new byte[SegmentSize];
            var otherReadTask = stream.ReadAsync(buffer2, 0, buffer2.Length, otherCts.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(50)); // let both reach the gated header call

            // Fire both cancellations from separate threads as close together as possible — the
            // adversarial timing the old snapshot-based check could lose.
            await Task.WhenAll(
                Task.Run(() => creatorCts.Cancel()),
                Task.Run(() => otherCts.Cancel()));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => creatorReadTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => otherReadTask.WaitAsync(TimeSpan.FromSeconds(5)));

            client.Release();

            // The shared construction genuinely failed (the creator's own token drove it), so the
            // memoized task must be cleared — deterministically, regardless of exactly how the two
            // cancellations interleaved. The clearing ContinueWith is attached to the real construction
            // task itself, which can settle to its terminal Canceled state a short async scheduling hop
            // AFTER creatorReadTask (which awaits a Task.WaitAsync wrapper around it, not that task
            // directly) has already thrown — so this polls briefly rather than asserting success on a
            // single immediate attempt. That is normal async-continuation latency, not the TOCTOU
            // window this test targets: it should settle within a couple of scheduler ticks, not
            // require the many-attempts/long-timeout tolerance the earlier bounded-retry test used
            // pre-fix.
            int? read = null;
            Exception? lastEx = null;
            for (var attempt = 0; attempt < 10 && read == null; attempt++)
            {
                try
                {
                    var buffer3 = new byte[SegmentSize];
                    var thisRead = await stream.ReadAsync(buffer3, 0, buffer3.Length, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2));
                    read = thisRead;
                    Assert.Equal((byte)3, buffer3[0]); // segment index 2's fill byte — real data, not stale
                }
                catch (Exception ex) when (attempt < 9 && (ex is OperationCanceledException or TimeoutException))
                {
                    // TimeoutException: this test's own WaitAsync wrapper, not the construction — a
                    // caller that joins a still-pending (not yet terminal) task waits for its real,
                    // network/timer-driven cancellation cascade to finish, which under this test's
                    // adversarial concurrent-cancellation load can occasionally take longer than one
                    // attempt's wrapper timeout. OperationCanceledException: same underlying construction
                    // genuinely still resolving. Neither is the TOCTOU/scheduling-gap window under test.
                    lastEx = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(20));
                }
            }

            Assert.True(read.HasValue,
                $"the fresh read must succeed once the failed construction's clear settles; last error: {lastEx}");
            Assert.Equal(SegmentSize, read!.Value);
        }
    }
}
