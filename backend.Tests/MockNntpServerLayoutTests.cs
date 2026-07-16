using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NzbWebDAV.Tools;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// The mock NNTP server used by --test-full-nzb must describe each segment truthfully.
/// It previously served one hardcoded article for every message ID, so every segment claimed
/// "=ypart begin=1 end=&lt;segmentSize&gt;". The client derives a segment's place in the file from
/// that header, so all segments landed at offset 0 and the file measured one segment long no matter
/// how many the NZB listed — the benchmark then streamed a single segment and reported numbers that
/// described nothing. See issue #15.
/// </summary>
public class MockNntpServerLayoutTests
{
    private const int SegmentSize = 1000;

    private sealed record ArticleHeaders(string YBegin, string YPart, string YEnd);

    /// <summary>
    /// Issues BODY for each segment id and returns the yEnc framing lines, skipping the payload.
    /// </summary>
    private static async Task<List<ArticleHeaders>> FetchHeadersAsync(int port, IEnumerable<string> segmentIds)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

        await reader.ReadLineAsync(); // greeting

        var results = new List<ArticleHeaders>();
        foreach (var id in segmentIds)
        {
            await writer.WriteLineAsync($"BODY <{id}>");

            var status = await reader.ReadLineAsync();
            Assert.StartsWith("222", status);

            var ybegin = await reader.ReadLineAsync();
            var ypart = await reader.ReadLineAsync();

            // Drain payload lines up to the =yend framing, then the "." terminator.
            string? line;
            while ((line = await reader.ReadLineAsync()) != null && !line.StartsWith("=yend")) { }
            var yend = line;
            await reader.ReadLineAsync(); // "."

            results.Add(new ArticleHeaders(ybegin!, ypart!, yend!));
        }

        return results;
    }

    [Fact]
    public async Task EachSegment_GetsItsOwnPartHeader()
    {
        var nzbPath = Path.Combine(Path.GetTempPath(), $"mock-layout-{Guid.NewGuid():N}.nzb");
        try
        {
            // 2500 bytes over 1000-byte segments: two full parts and a 500-byte remainder.
            var layout = await MockNzbGenerator.GenerateAsync(nzbPath, totalSize: 2500, segmentSize: SegmentSize);
            var file = Assert.Single(layout.Files);
            Assert.Equal(3, file.SegmentIds.Count);

            using var server = new MockNntpServer(port: 0, latencyMs: 0, segmentSize: SegmentSize, jitterMs: 0,
                timeoutRate: 0, layout: layout);
            server.Start();

            var headers = await FetchHeadersAsync(server.Port, file.SegmentIds);

            // =ybegin size= is the whole file, identical across parts — that is what the client
            // reports as the file's length.
            Assert.All(headers, h => Assert.Contains("size=2500", h.YBegin));
            Assert.Contains("part=1", headers[0].YBegin);
            Assert.Contains("part=2", headers[1].YBegin);
            Assert.Contains("part=3", headers[2].YBegin);

            // =ypart is 1-based and inclusive: the client reads offset as (begin - 1) and length as
            // (end - begin + 1). These must tile the file with no gap and no overlap.
            Assert.Equal("=ypart begin=1 end=1000", headers[0].YPart);
            Assert.Equal("=ypart begin=1001 end=2000", headers[1].YPart);
            Assert.Equal("=ypart begin=2001 end=2500", headers[2].YPart);

            // The trailing segment carries the remainder, not a full segment.
            Assert.Equal("=yend size=1000 part=1", headers[0].YEnd);
            Assert.Equal("=yend size=1000 part=2", headers[1].YEnd);
            Assert.Equal("=yend size=500 part=3", headers[2].YEnd);
        }
        finally
        {
            if (File.Exists(nzbPath)) File.Delete(nzbPath);
        }
    }

    /// <summary>
    /// The regression itself: distinct segments must not return byte-identical part headers.
    /// Before the fix every segment reported begin=1, collapsing the file to a single segment.
    /// </summary>
    [Fact]
    public async Task DistinctSegments_DoNotShareTheSameOffset()
    {
        var nzbPath = Path.Combine(Path.GetTempPath(), $"mock-layout-{Guid.NewGuid():N}.nzb");
        try
        {
            var layout = await MockNzbGenerator.GenerateAsync(nzbPath, totalSize: 10_000, segmentSize: SegmentSize);
            var file = Assert.Single(layout.Files);

            using var server = new MockNntpServer(port: 0, latencyMs: 0, segmentSize: SegmentSize, jitterMs: 0,
                timeoutRate: 0, layout: layout);
            server.Start();

            var headers = await FetchHeadersAsync(server.Port, file.SegmentIds);

            var distinctParts = new HashSet<string>();
            foreach (var h in headers) distinctParts.Add(h.YPart);

            Assert.Equal(file.SegmentIds.Count, distinctParts.Count);
        }
        finally
        {
            if (File.Exists(nzbPath)) File.Delete(nzbPath);
        }
    }

    /// <summary>
    /// The NZB's per-segment byte counts must match what the server serves, so the file size the
    /// client computes from the NZB agrees with the size it computes from the articles.
    /// </summary>
    [Fact]
    public async Task GeneratedNzb_SegmentBytes_MatchServedPartSizes()
    {
        var nzbPath = Path.Combine(Path.GetTempPath(), $"mock-layout-{Guid.NewGuid():N}.nzb");
        try
        {
            var layout = await MockNzbGenerator.GenerateAsync(nzbPath, totalSize: 2500, segmentSize: SegmentSize);
            var nzbText = await File.ReadAllTextAsync(nzbPath);

            Assert.Contains("bytes='1000' number='1'", nzbText);
            Assert.Contains("bytes='1000' number='2'", nzbText);
            Assert.Contains("bytes='500' number='3'", nzbText);

            var file = Assert.Single(layout.Files);
            Assert.Equal(2500, file.FileSize);
        }
        finally
        {
            if (File.Exists(nzbPath)) File.Delete(nzbPath);
        }
    }
}
