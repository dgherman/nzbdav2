using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using Usenet.Nzb;
using UsenetSharp.Models;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Regression coverage for the RAR-volume-boundary freeze: sequential playback of a RAR-multipart
/// file cold-rebuilt the next volume's stream (fresh connection acquire, prefetch window reset to
/// empty) only after the current volume's stream returned EOF, because CombinedStream's forward
/// end-of-part advance in ReadAsync never populated (or consulted) its stream cache — that cache is
/// only ever written by Seek() switching parts. See DavMultipartFileStream.cs (maxCachedStreams: 0)
/// and CombinedStream.cs's ReadAsync exhaustion branch (~line 130, "Part {PartIndex} exhausted.").
///
/// The fix pre-warms the next part's stream (via IWarmableStream) once the current part's remaining
/// bytes fall inside a tail window, so by the time the boundary is actually crossed the next part's
/// stream is already fetching instead of starting cold.
///
/// [Collection(BufferedStreamCollection.Name)]: the real-fetch test below constructs an actual
/// BufferedSegmentStream via NzbFileStream, which touches the process-wide concurrent-stream slot
/// semaphore (BufferedSegmentStream.SetMaxConcurrentStreams) — same serialization every other test
/// touching that static state uses (see StreamChurnTests, BufferedSegmentStreamDisposeTests).
/// </summary>
[Collection(BufferedStreamCollection.Name)]
public class CombinedStreamPrewarmTests
{
    /// <summary>A part's stream that records when WarmupAsync and ReadAsync are called, so tests can
    /// order-check "warmed before crossed" without depending on real network I/O.</summary>
    private sealed class WarmupTrackingStream : Stream, IWarmableStream
    {
        private readonly byte[] _payload;
        private int _position;
        private readonly TaskCompletionSource _warmupStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WarmupTrackingStream(byte[] payload) => _payload = payload;

        public int WarmupCallCount { get; private set; }
        public int ReadCallCount { get; private set; }
        public Task WarmupStarted => _warmupStarted.Task;

        public Task WarmupAsync(CancellationToken ct)
        {
            WarmupCallCount++;
            _warmupStarted.TrySetResult();
            return Task.CompletedTask;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadCallCount++;
            var n = Math.Min(count, _payload.Length - _position);
            if (n <= 0) return Task.FromResult(0);
            Array.Copy(_payload, _position, buffer, offset, n);
            _position += n;
            return Task.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A plain sequential-read part stream, no warmup support — stands in for a part CombinedStream
    /// is currently reading through.</summary>
    private sealed class PlainStream : Stream
    {
        private readonly byte[] _payload;
        private int _position;

        public PlainStream(byte[] payload) => _payload = payload;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var n = Math.Min(count, _payload.Length - _position);
            if (n <= 0) return Task.FromResult(0);
            Array.Copy(_payload, _position, buffer, offset, n);
            _position += n;
            return Task.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    public async Task NextVolume_IsWarmedBeforeTheBoundaryIsCrossed()
    {
        var part0Payload = new byte[10];
        Array.Fill(part0Payload, (byte)0xAA);
        var part1Payload = new byte[10];
        Array.Fill(part1Payload, (byte)0xBB);

        var part1Factory = new WarmupTrackingStream(part1Payload);
        var part1FactoryCalls = 0;

        var parts = new List<(Func<Task<Stream>> StreamFactory, long Length)>
        {
            (() => Task.FromResult<Stream>(new PlainStream(part0Payload)), part0Payload.Length),
            (() => { part1FactoryCalls++; return Task.FromResult<Stream>(part1Factory); }, part1Payload.Length),
        };

        // maxCachedStreams: 0, matching DavMultipartFileStream's RAR-multipart configuration — proves
        // the warm handoff does not depend on CombinedStream's Seek-only stream cache.
        await using var combined = new CombinedStream(parts, maxCachedStreams: 0);

        var buffer = new byte[part0Payload.Length];
        var read = await ReadFully(combined, buffer).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(part0Payload.Length, read);
        Assert.Equal(part0Payload, buffer);

        // Part 1's stream must already be warming by the time part 0 is fully consumed — before
        // CombinedStream has crossed the boundary or issued a single read against it.
        await part1Factory.WarmupStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, part1Factory.WarmupCallCount);
        Assert.Equal(0, part1Factory.ReadCallCount);

        // Cross the boundary: this must reuse the already-warmed stream, not construct a second one.
        var buffer2 = new byte[part1Payload.Length];
        var read2 = await ReadFully(combined, buffer2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(part1Payload.Length, read2);
        Assert.Equal(part1Payload, buffer2);
        Assert.Equal(1, part1FactoryCalls); // memoized — no cold rebuild, no duplicate construction
        Assert.Equal(1, part1Factory.WarmupCallCount); // warmed exactly once
    }

    [Fact]
    public async Task LastVolume_IsNeverWarmed_ThereIsNoNextPart()
    {
        var payload = new byte[10];
        Array.Fill(payload, (byte)0xCC);

        var parts = new List<(Func<Task<Stream>> StreamFactory, long Length)>
        {
            (() => Task.FromResult<Stream>(new PlainStream(payload)), payload.Length),
        };

        await using var combined = new CombinedStream(parts, maxCachedStreams: 0);
        var buffer = new byte[payload.Length];
        var read = await ReadFully(combined, buffer).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload.Length, read);

        // No further part exists — reading past EOF must not throw despite the pre-warm trigger
        // running on every successful read.
        var tail = new byte[1];
        var eof = await combined.ReadAsync(tail, 0, 1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, eof);
    }

    /// <summary>Counts real segment fetches so a test can observe genuine background-fetch activity,
    /// not just that some method returned. A small delay per fetch keeps "nothing fetched yet" and
    /// "fetching has started" observably distinct instead of racing a same-tick completion.</summary>
    private sealed class CountingSegmentNntpClient : INntpClient
    {
        private readonly int _segmentSize;
        private int _fetchCount;

        public CountingSegmentNntpClient(int segmentSize) => _segmentSize = segmentSize;

        public int FetchCount => Volatile.Read(ref _fetchCount);

        public async Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
        {
            Interlocked.Increment(ref _fetchCount);
            await Task.Delay(15, ct).ConfigureAwait(false);
            var index = int.Parse(segmentId.Split('-')[1].Split('@')[0]);
            var header = new UsenetYencHeader
            {
                FileName = "volume.r00",
                FileSize = 0,
                LineLength = 128,
                PartNumber = index + 1,
                TotalParts = 1,
                PartSize = _segmentSize,
                PartOffset = (long)_segmentSize * index,
            };
            return new YencHeaderStream(header, null, new MemoryStream(new byte[_segmentSize]));
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
    public async Task NextVolume_RealBufferedSegmentStream_StartsFetchingBeforeTheBoundaryIsCrossed()
    {
        // End-to-end through the real production topology: DavMultipartFileStream wraps each RAR
        // volume's NzbFileStream in a LimitedLengthStream and hands it to CombinedStream as a part
        // factory (see DavMultipartFileStream.GetCombinedStream). This is what NextVolume_IsWarmed-
        // BeforeTheBoundaryIsCrossed above does NOT prove: WarmupTrackingStream's WarmupAsync
        // completes instantly and does no real fetching, so that test only demonstrates call
        // ordering. Here the "next volume" is a real NzbFileStream backed by a real
        // BufferedSegmentStream, and the assertion is on actual (fake-network) segment fetches
        // landing — i.e. the prefetch window is genuinely non-empty — before any read crosses into it.
        const int segmentSize = 4096;
        const int part1SegmentCount = 40; // > concurrentConnections, so the full buffered path engages
        const int concurrentConnections = 4;

        var part0Payload = new byte[10];
        Array.Fill(part0Payload, (byte)0xAA);

        var client = new CountingSegmentNntpClient(segmentSize);
        var segmentIds = Enumerable.Range(0, part1SegmentCount).Select(i => $"seg-{i}@test").ToArray();
        var context = new ConnectionUsageContext(
            ConnectionUsageType.Streaming, new ConnectionUsageDetails { Text = "prewarm-integration-test" });

        BufferedSegmentStream.SetMaxConcurrentStreams(4);
        try
        {
            Func<Task<Stream>> part0Factory = () => Task.FromResult<Stream>(new PlainStream(part0Payload));
            Func<Task<Stream>> part1Factory = () =>
            {
                var nzbStream = new NzbFileStream(
                    segmentIds, fileSize: (long)segmentSize * part1SegmentCount, client,
                    concurrentConnections, usageContext: context);
                return Task.FromResult<Stream>(nzbStream.LimitLength((long)segmentSize * part1SegmentCount));
            };
            var parts = new List<(Func<Task<Stream>> StreamFactory, long Length)>
            {
                (part0Factory, part0Payload.Length),
                (part1Factory, (long)segmentSize * part1SegmentCount),
            };

            // maxCachedStreams: 0, matching DavMultipartFileStream's real RAR-multipart configuration.
            await using var combined = new CombinedStream(parts, maxCachedStreams: 0);

            var buffer = new byte[part0Payload.Length];
            var read = await ReadFully(combined, buffer).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(part0Payload.Length, read);

            // The pre-warm must start real fetch activity for volume 1 before CombinedStream ever
            // crosses into it — wait for that here, before issuing any read that would cross.
            var started = await TestAsync.WaitUntil(() => client.FetchCount > 0, TimeSpan.FromSeconds(5));
            Assert.True(started,
                "pre-warm did not start any real segment fetches for the next volume before the boundary was crossed");

            // Crossing the boundary now reads from a stream whose background fetch already started —
            // not one built cold at this exact moment.
            var buffer2 = new byte[segmentSize];
            var read2 = await ReadFully(combined, buffer2).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(buffer2.Length, read2);
        }
        finally
        {
            BufferedSegmentStream.SetMaxConcurrentStreams(32); // restore the assembly-wide default
        }
    }
}
