using System;
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

public class BufferedSegmentStreamDisposeTests
{
    private const int SegmentSize = 1024;

    /// <summary>
    /// Hands out segment streams whose disposal throws, and nothing else.
    /// </summary>
    private sealed class ThrowingDisposeNntpClient : INntpClient
    {
        private readonly Func<Exception> _disposeException;
        public ThrowingDisposeNntpClient(Func<Exception> disposeException) => _disposeException = disposeException;

        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
        {
            var header = new UsenetYencHeader
            {
                FileName = "test.mkv",
                FileSize = SegmentSize * 2,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 2,
                PartSize = SegmentSize,
                PartOffset = 0,
            };
            var payload = new byte[SegmentSize];
            Array.Fill(payload, (byte)0xAB);
            return Task.FromResult(new YencHeaderStream(header, null, new ThrowingDisposeStream(_disposeException(), payload)));
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

    [Fact]
    public async Task SegmentDisposeThrowing_DoesNotAbortTheStream()
    {
        // Regression: `finally { await stream.DisposeAsync(); }` was unguarded. Under memory pressure
        // the dispose chain itself allocates (the connection-pool stats event builds a string), so it
        // threw OutOfMemoryException from the finally — which replaces the exception being handled and
        // escapes the whole method, past the OOM retry handler and past graceful degradation. The
        // worker's outer catch then aborted the entire stream ("Critical worker error ... Aborting
        // stream") over one segment's disposal. Observed on the NAS 2026-07-16.
        var segmentIds = new[] { "seg-0@test", "seg-1@test" };
        var segmentSizes = new long[] { SegmentSize, SegmentSize };
        var client = new ThrowingDisposeNntpClient(() => new OutOfMemoryException());
        var context = new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, new ConnectionUsageDetails { Text = "test" });

        await using var stream = new BufferedSegmentStream(
            segmentIds,
            fileSize: SegmentSize * 2,
            client,
            concurrentConnections: 2,
            bufferSegmentCount: 4,
            cancellationToken: CancellationToken.None,
            usageContext: context,
            segmentSizes: segmentSizes);

        var buffer = new byte[SegmentSize * 2];
        var read = await ReadFully(stream, buffer).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(SegmentSize * 2, read);
        Assert.All(buffer, b => Assert.Equal(0xAB, b)); // real payload, not zero-filled degradation
    }
}
