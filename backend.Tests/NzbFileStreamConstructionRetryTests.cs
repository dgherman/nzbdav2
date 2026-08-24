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
/// Regression coverage for the second cross-review round on the RAR-volume pre-warm fix:
/// NzbFileStream.EnsureInnerStreamAsync memoizes its construction task onto _innerStreamTask so a
/// real read and a background pre-warm never build two streams for the same position. Before this
/// fix, a construction that threw (a cancelled read, a transient header-fetch failure) left that
/// failed task memoized forever — every subsequent read on the same instance re-awaited the same
/// dead task instead of retrying, permanently breaking playback after one transient error. The fix
/// clears the memoized task on failure (compare-and-clear under the gate) so the next caller starts
/// fresh. See EnsureInnerStreamAsync's catch block in NzbFileStream.cs.
/// </summary>
[Collection(BufferedStreamCollection.Name)]
public class NzbFileStreamConstructionRetryTests
{
    private const int SegmentSize = 4096;
    private const int SegmentCount = 8;

    /// <summary>Throws on the first header lookup (used to resolve a non-zero Seek to a segment
    /// index), succeeds on every call after. Segment data fetches always succeed.</summary>
    private sealed class FlakyOnceNntpClient : INntpClient
    {
        private int _headerCalls;
        public int HeaderCallCount => Volatile.Read(ref _headerCalls);

        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _headerCalls);
            if (call == 1) throw new IOException("simulated transient header-fetch failure");
            return Task.FromResult(BuildHeader(segmentId));
        }

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

    [Fact]
    public async Task FailedConstruction_DoesNotPoisonSubsequentReads()
    {
        var client = new FlakyOnceNntpClient();
        var ids = Enumerable.Range(0, SegmentCount).Select(i => $"seg-{i}@test").ToArray();
        var context = new ConnectionUsageContext(
            ConnectionUsageType.Streaming, new ConnectionUsageDetails { Text = "construction-retry-test" });

        BufferedSegmentStream.SetMaxConcurrentStreams(4);
        try
        {
            await using var stream = new NzbFileStream(
                ids, fileSize: (long)SegmentSize * SegmentCount, client,
                concurrentConnections: 3, usageContext: context);

            // ONE-TIME setup: move off position 0 so construction has to resolve the read through
            // SeekSegment -> GetSegmentYencHeaderAsync — position 0 takes GetFileStream's rangeStart==0
            // branch straight into GetCombinedStream and never calls the client at all, so it could not
            // exercise the failure path this test targets. This is the ONLY Seek() call anywhere in
            // this test — it runs before either read below, not between them.
            stream.Seek(SegmentSize * 2L, SeekOrigin.Begin);
            var positionBeforeFirstAttempt = stream.Position;

            var buffer = new byte[SegmentSize];
            await Assert.ThrowsAsync<IOException>(
                () => stream.ReadAsync(buffer, 0, buffer.Length).WaitAsync(TimeSpan.FromSeconds(10)));

            // No Seek() happens between the failed attempt and the retry below — a failed ReadAsync
            // never advances _position, and this test does not call Seek() again, so Position is
            // provably unchanged. The retry is therefore a bare ReadAsync at the SAME position with NO
            // intervening Seek(): the only thing that can un-stick a memoized _innerStreamTask here is
            // EnsureInnerStreamAsync's own catch-block compare-and-clear, not Seek()'s
            // InvalidateInnerStream (which cannot be what's running — Seek() is not called again).
            Assert.Equal(positionBeforeFirstAttempt, stream.Position);
            var read = await stream.ReadAsync(buffer, 0, buffer.Length).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(SegmentSize, read);
            Assert.Equal((byte)3, buffer[0]); // segment index 2's fill byte (index + 1) — real data, not empty/stale
            Assert.Equal(2, client.HeaderCallCount); // one failed lookup, one successful retry — not memoized past the failure
        }
        finally
        {
            BufferedSegmentStream.SetMaxConcurrentStreams(32); // restore the assembly-wide default
        }
    }
}
