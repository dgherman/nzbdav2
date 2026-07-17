using System;
using System.IO;
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

public class BufferedSegmentStreamPrefetchWindowTests
{
    private const int SegmentSize = 1024;
    private const int SegmentCount = 500;

    /// <summary>
    /// Serves a fixed payload for any segment and counts how many segments were asked for.
    /// </summary>
    private sealed class CountingNntpClient : INntpClient
    {
        private int _served;
        public int Served => Volatile.Read(ref _served);

        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
        {
            Interlocked.Increment(ref _served);
            var header = new UsenetYencHeader
            {
                FileName = "test.mkv",
                FileSize = (long)SegmentSize * SegmentCount,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = SegmentCount,
                PartSize = SegmentSize,
                PartOffset = 0,
            };
            var payload = new byte[SegmentSize];
            Array.Fill(payload, (byte)0xAB);
            return Task.FromResult(new YencHeaderStream(header, null, new MemoryStream(payload)));
        }

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public async Task Prefetch_StopsAtTheWindow_WhenTheReaderStops()
    {
        // Regression for issue #19. Every fetched segment parks a ~1MB pooled buffer in
        // segmentSlots, which is sized to the whole file. The bounded channel throttles only the
        // ordering task, which sits downstream of the slots, so nothing tied the fetch rate to the
        // read rate: workers raced to the end of the file and the resident set scaled with file
        // size rather than with bufferSegmentCount. Measured in the mock harness at 200 MB: 293
        // segments in the file, 299 buffers checked out at peak — the whole file was resident.
        const int connections = 4;
        const int bufferSegments = 8;

        // Pin the window. Production floors it at MinPrefetchWindowSegments (300) because too small
        // a window starves the reader and kills playback — but that floor is a separate concern from
        // the bounding this test covers, and at 300 the window would exceed the 500-segment file and
        // the test could not tell bounded from unbounded. Test 2 covers the floor.
        BufferedSegmentStream.SetPrefetchWindow(bufferSegments + connections);
        try
        {

        var segmentIds = new string[SegmentCount];
        var segmentSizes = new long[SegmentCount];
        for (var i = 0; i < SegmentCount; i++)
        {
            segmentIds[i] = $"seg-{i}@test";
            segmentSizes[i] = SegmentSize;
        }

        var client = new CountingNntpClient();
        var context = new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, new ConnectionUsageDetails { Text = "test" });

        await using var stream = new BufferedSegmentStream(
            segmentIds,
            fileSize: (long)SegmentSize * SegmentCount,
            client,
            concurrentConnections: connections,
            bufferSegmentCount: bufferSegments,
            cancellationToken: CancellationToken.None,
            usageContext: context,
            segmentSizes: segmentSizes);

        // Consume exactly one segment, then stop reading and let the fetchers run.
        var buffer = new byte[SegmentSize];
        var read = await ReadFully(stream, buffer).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(SegmentSize, read);

        await Task.Delay(TimeSpan.FromSeconds(2));

        // The reader has consumed one segment, so prefetch may run a window ahead of it and no
        // further. The exact ceiling depends on in-flight timing; what matters is that it is a
        // function of the configured window and not of the file length.
        var ceiling = (bufferSegments + connections) * 2;
        Assert.InRange(client.Served, 1, ceiling);

        }
        finally
        {
            BufferedSegmentStream.SetPrefetchWindow(0);
        }
    }

    [Fact]
    public async Task Prefetch_AppliesTheFloor_WhenTheComputedWindowIsTooSmall()
    {
        // The other half of issue #19. Bounding prefetch is necessary but not sufficient: PR #21
        // shipped an effective window of 90 for 30 connections and the second concurrent video would
        // not play at all — the reader starved, playback died on NNTP body-read timeouts, and the
        // retries tripped the providers' breakers. A window of 300 ran three concurrent production
        // streams clean. So a computed window below the floor must be raised to it.
        const int connections = 4;
        const int bufferSegments = 8;
        var computedWindow = bufferSegments + connections; // 12 — far below the 300 floor

        var segmentIds = new string[SegmentCount];
        var segmentSizes = new long[SegmentCount];
        for (var i = 0; i < SegmentCount; i++)
        {
            segmentIds[i] = $"seg-{i}@test";
            segmentSizes[i] = SegmentSize;
        }

        var client = new CountingNntpClient();
        var context = new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, new ConnectionUsageDetails { Text = "test" });

        BufferedSegmentStream.SetPrefetchWindow(0); // 0 = auto, so the floor decides
        await using var stream = new BufferedSegmentStream(
            segmentIds,
            fileSize: (long)SegmentSize * SegmentCount,
            client,
            concurrentConnections: connections,
            bufferSegmentCount: bufferSegments,
            cancellationToken: CancellationToken.None,
            usageContext: context,
            segmentSizes: segmentSizes);

        var buffer = new byte[SegmentSize];
        var read = await ReadFully(stream, buffer).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(SegmentSize, read);

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Above the computed window: the floor was applied rather than the raw computation.
        Assert.True(client.Served > computedWindow * 2,
            $"expected the {BufferedSegmentStream.MinPrefetchWindowSegments}-segment floor to apply, " +
            $"but only {client.Served} segments were served — that is the computed window ({computedWindow}), not the floor.");

        // Still a function of the window and not of the file length: the file is 500 segments and
        // the floor is 300, so a bounded stream cannot have fetched all of them.
        Assert.True(client.Served < SegmentCount,
            $"prefetch served {client.Served} of {SegmentCount} segments — the whole file, i.e. unbounded.");
    }

    private static async Task<int> ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
