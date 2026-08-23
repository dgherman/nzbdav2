using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Streams;
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
/// </summary>
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
}
