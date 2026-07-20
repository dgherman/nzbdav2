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

[Collection(BufferedStreamCollection.Name)]
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

    // The other half of issue #19 is now pure arithmetic in ComputePrefetchWindow, tested directly.
    // Bounding prefetch is necessary but not sufficient: too small a window starves the reader (PR #21
    // shipped an effective 90 for 30 connections and the second concurrent video would not play at
    // all). The floor is denominated in BYTES because segment size varies ~8x between releases, so a
    // fixed segment count buys wildly different memory for the same starvation guarantee.

    [Theory]
    // 4 MiB segments (~Backrooms, whose real 4.19 MB average yields 64): 256 MiB / 4 MiB = 64 segments.
    // Validated clean on production at a hand-set 51; 64 is the same region with more headroom. The
    // fixed 300-segment floor it replaces held 2.4 GB here for the identical guarantee.
    [InlineData(4L * 1024 * 1024, 64)]
    // 1 MiB segments: 256 MiB / 1 MiB = 256 segments.
    [InlineData(1L * 1024 * 1024, 256)]
    // 512 KiB segments (~Alone's 717 KB, which yields 374): smaller segments buy proportionally more of
    // them for the same bytes in flight. 256 MiB / 512 KiB = 512 segments.
    [InlineData(512L * 1024, 512)]
    public void ByteFloor_ConvertsTheBudgetUsingAverageSegmentSize(long avgSegmentSize, int expectedWindow)
    {
        // computedWindow small so the floor is what binds; no explicit override.
        var (window, source) = BufferedSegmentStream.ComputePrefetchWindow(
            computedWindow: 12, configuredWindow: 0, avgSegmentSize: avgSegmentSize);

        Assert.Equal("byte-floor", source);
        Assert.Equal(expectedWindow, window);
        // The whole point: the byte budget holds regardless of segment size.
        Assert.InRange(
            window * avgSegmentSize,
            BufferedSegmentStream.MinPrefetchWindowBytes - avgSegmentSize,
            BufferedSegmentStream.MinPrefetchWindowBytes + avgSegmentSize);
    }

    [Fact]
    public void ByteFloor_NeverStarvesParallelism_WhenComputedWindowExceedsIt()
    {
        // A large-segment file whose byte budget buys only a few segments must still keep enough in
        // flight to feed every worker: the floor never drops the window below the computed value.
        // 32 MB segments => 256 MB / 32 MB = 8, but the computed window is 40.
        var (window, source) = BufferedSegmentStream.ComputePrefetchWindow(
            computedWindow: 40, configuredWindow: 0, avgSegmentSize: 32L * 1024 * 1024);

        Assert.Equal("computed", source);
        Assert.Equal(40, window);
    }

    [Fact]
    public void ExplicitOverride_WinsVerbatim_OverTheByteFloor()
    {
        var (window, source) = BufferedSegmentStream.ComputePrefetchWindow(
            computedWindow: 12, configuredWindow: 150, avgSegmentSize: 4_194_304);

        Assert.Equal("configured", source);
        Assert.Equal(150, window);
    }

    [Fact]
    public void UnknownAverageSize_FallsBackToTheSegmentFloor()
    {
        // No size table or an empty stream: the byte budget can't be converted, so the segment-count
        // fallback applies rather than a division by zero.
        var (window, source) = BufferedSegmentStream.ComputePrefetchWindow(
            computedWindow: 12, configuredWindow: 0, avgSegmentSize: 0);

        Assert.Equal("byte-floor", source);
        Assert.Equal(BufferedSegmentStream.MinPrefetchWindowSegments, window);
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
