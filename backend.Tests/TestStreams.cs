using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests;

/// <summary>
/// Stands in for BufferedSegmentStream: hands out a scripted chunk, then blocks until the test
/// releases it or the stream is disposed. Disposal completes any pending read with a cancellation,
/// which is how the real buffered stream unblocks its pump.
/// </summary>
internal sealed class FakeInnerStream : Stream
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _payload;
    private readonly bool _gateFirstRead;
    private readonly Exception? _throwOnRead;
    private int _reads;

    public FakeInnerStream(int payloadBytes = 0, bool gateFirstRead = false, Exception? throwOnRead = null)
    {
        _payload = new byte[payloadBytes];
        _gateFirstRead = gateFirstRead;
        _throwOnRead = throwOnRead;
    }

    public int ReadCount => Volatile.Read(ref _reads);
    public Task FirstReadEntered => _firstReadEntered.Task;

    /// <summary>Lets a gated first read return its payload.</summary>
    public void ReleaseFirstRead() => _release.TrySetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var readNumber = Interlocked.Increment(ref _reads);
        if (readNumber == 1) _firstReadEntered.TrySetResult();

        if (_throwOnRead != null) throw _throwOnRead;

        if (readNumber == 1)
        {
            if (_gateFirstRead) await Task.WhenAny(_release.Task, _disposed.Task).ConfigureAwait(false);
            if (_disposed.Task.IsCompleted) throw new OperationCanceledException();
            var n = Math.Min(count, _payload.Length);
            Array.Copy(_payload, 0, buffer, offset, n);
            return n;
        }

        // Subsequent reads block until disposal, mirroring a stream waiting on more segments.
        await _disposed.Task.ConfigureAwait(false);
        throw new OperationCanceledException();
    }

    protected override void Dispose(bool disposing)
    {
        _disposed.TrySetResult();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Endless source of bytes that records Touch() calls, so tests can assert the pump keeps the inner
/// stream's idle watchdog alive while it is paused on backpressure.
/// </summary>
internal sealed class TouchCountingStream : Stream, ITouchableStream
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _chunkSize;
    private int _touches;

    public TouchCountingStream(int chunkSize = 4096) => _chunkSize = chunkSize;

    public int TouchCount => Volatile.Read(ref _touches);
    public void Touch() => Interlocked.Increment(ref _touches);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed.Task.IsCompleted) throw new OperationCanceledException();
        return Task.FromResult(Math.Min(count, _chunkSize));
    }

    protected override void Dispose(bool disposing)
    {
        _disposed.TrySetResult();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Serves a payload, then throws from DisposeAsync — reproducing a dispose chain that itself
/// allocates and fails under memory pressure (the connection-pool stats event builds a string).
/// A throw from a finally escapes past every handler in the fetch retry loop.
/// </summary>
internal sealed class ThrowingDisposeStream : Stream
{
    private readonly Exception _disposeException;
    private readonly byte[] _payload;
    private int _position;

    public ThrowingDisposeStream(Exception disposeException, byte[] payload)
    {
        _disposeException = disposeException;
        _payload = payload;
    }

    public override ValueTask DisposeAsync() => throw _disposeException;
    protected override void Dispose(bool disposing) => throw _disposeException;

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = Math.Min(count, _payload.Length - _position);
        if (n <= 0) return Task.FromResult(0);
        Array.Copy(_payload, _position, buffer, offset, n);
        _position += n;
        return Task.FromResult(n);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = Math.Min(buffer.Length, _payload.Length - _position);
        if (n <= 0) return ValueTask.FromResult(0);
        _payload.AsSpan(_position, n).CopyTo(buffer.Span);
        _position += n;
        return ValueTask.FromResult(n);
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

internal static class TestAsync
{
    public static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return condition();
    }
}
