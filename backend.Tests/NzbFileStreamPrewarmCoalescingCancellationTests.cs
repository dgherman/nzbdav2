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
/// Regression coverage for a cross-review finding on the pre-warm slot-priority fix
/// (BufferedSegmentStreamPrewarmSlotPriorityTests): NzbFileStream.EnsureInnerStreamAsync memoizes its
/// construction task onto _innerStreamTask so a real read and a background pre-warm share one
/// construction per generation. A pre-warm-tagged construction (WarmupAsync -> isPrewarm: true) can be
/// mid-flight on TryAcquireSlot's bounded slot wait when a REAL read for the SAME NzbFileStream
/// instance arrives — e.g. the actual boundary crossing the pre-warm was preparing for. Before this
/// fix, that real read found _innerStreamTask already set and did a bare `await task`, which:
///   (1) had no way to observe the real read's OWN cancellation token — only the pre-warm caller's
///       token governed the in-flight TryAcquireSlot(true, prewarmCt) wait, so a cancelled real read
///       could not bail out of a wait it never asked for, and
///   (2) if it somehow could bail, risked clearing the shared _innerStreamTask out from under the
///       pre-warm caller that is still legitimately awaiting the very same task.
///
/// The fix wraps the shared await in Task.WaitAsync(ct) so each caller's own token governs its own
/// wait, and only clears the memoized task when the CONSTRUCTION itself (not just this caller's wait
/// on it) actually failed — distinguished via the synthesized exception's CancellationToken.
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
        {
            var header = BuildHeader(segmentId);
            var index = ParseIndex(segmentId);
            var payload = new byte[SegmentSize];
            Array.Fill(payload, (byte)(index + 1));
            return Task.FromResult(new YencHeaderStream(header, null, new MemoryStream(payload)));
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

    private static NzbFileStream MakeStream(INntpClient client)
    {
        var ids = Enumerable.Range(0, SegmentCount).Select(i => $"seg-{i}@test").ToArray();
        // No DavItemId: skips the shared-stream path entirely, so TryAcquireSlot(isPrewarm, ct) is
        // called directly from GetCombinedStream's direct-path branch — the simplest way to put a
        // real slot wait on the critical path this test exercises.
        var context = new ConnectionUsageContext(
            ConnectionUsageType.Streaming, new ConnectionUsageDetails { Text = "coalescing-cancellation-test" });
        return new NzbFileStream(
            ids, fileSize: (long)SegmentSize * SegmentCount, client,
            concurrentConnections: 3, usageContext: context);
    }

    [Fact]
    public async Task LiveRead_CoalescedOntoPendingPrewarm_RespectsItsOwnCancellationToken()
    {
        BufferedSegmentStream.SetMaxConcurrentStreams(1);
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(30);
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
            // Task.Run: TryAcquireSlot's bounded wait is a genuine thread-blocking call (see
            // BufferedSegmentStream.TryAcquireSlot's isPrewarm branch) with no prior await point to
            // suspend at, so calling WarmupAsync directly on this thread would block the test itself.
            // Production only ever reaches isPrewarm: true from CombinedStream.PrewarmPartAsync's own
            // Task.Run for exactly this reason — mirror that here.
            using var prewarmCts = new CancellationTokenSource();
            var warmupTask = Task.Run(() => stream.WarmupAsync(prewarmCts.Token));
            await Task.Delay(TimeSpan.FromMilliseconds(300)); // let it actually start waiting on the slot

            // A real read for the SAME instance arrives now (the boundary crossing this pre-warm was
            // preparing for) and coalesces onto the same in-flight construction. It has its own,
            // independent cancellation token.
            using var liveCts = new CancellationTokenSource();
            var buffer = new byte[SegmentSize];
            var readTask = stream.ReadAsync(buffer, 0, buffer.Length, liveCts.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            liveCts.Cancel();

            // Must observe ITS OWN token promptly — not sit through the pre-warm's up-to-30s wait,
            // and not throw for an unrelated reason. Task.WaitAsync(ct)'s cancellation surfaces as
            // TaskCanceledException (a subtype of OperationCanceledException) — assert the base type
            // since either is a correct, expected cancellation.
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => readTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(liveCts.Token, ex.CancellationToken);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 3000,
                $"the live read's own cancellation should unblock it promptly, took {sw.ElapsedMilliseconds}ms");

            // The shared construction itself must be untouched by the live read's own cancellation:
            // once the slot frees, the ORIGINAL pre-warm caller still gets a real result.
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
    public async Task LiveRead_CoalescedOntoPendingPrewarm_StillSucceeds_WhenNotCancelled()
    {
        BufferedSegmentStream.SetMaxConcurrentStreams(1);
        BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(10);
        try
        {
            var holder = BufferedSegmentStream.TryAcquireSlot();
            Assert.NotNull(holder);

            var client = new InstantNntpClient();
            await using var stream = MakeStream(client);

            using var prewarmCts = new CancellationTokenSource();
            var warmupTask = stream.WarmupAsync(prewarmCts.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            var buffer = new byte[SegmentSize];
            var readTask = stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            // Neither side is cancelled — releasing the slot should let BOTH the pre-warm and the
            // coalesced live read complete successfully off the one shared construction.
            holder!.Release();

            await warmupTask.WaitAsync(TimeSpan.FromSeconds(10));
            var read = await readTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(SegmentSize, read);
            Assert.Equal((byte)1, buffer[0]); // segment index 0's fill byte — real data
        }
        finally
        {
            BufferedSegmentStream.SetMaxConcurrentStreams(32);
            BufferedSegmentStream.PrewarmSlotAcquireTimeout = TimeSpan.FromSeconds(5);
        }
    }
}
