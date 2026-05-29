using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Per-reader cursor into a SharedStreamEntry's ring buffer.
/// One handle per HTTP request. Implements Stream as a drop-in replacement
/// for BufferedSegmentStream in CombinedStream wrappers.
/// </summary>
public class SharedStreamHandle : Stream
{
    private readonly SharedStreamEntry _entry;
    private readonly int _handleId;
    private long _position;
    private bool _detached;
    private bool _disposed;

    internal SharedStreamHandle(SharedStreamEntry entry, long startPosition, int handleId)
    {
        _entry = entry;
        _position = startPosition;
        _handleId = handleId;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false; // NzbFileStream handles seeks by disposing/recreating
    public override bool CanWrite => false;
    public override long Length => _entry.StreamLength;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// True when this reader has fallen behind the ring buffer window
    /// and should be replaced with an unbuffered fallback.
    /// </summary>
    public bool IsDetached => _detached;

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed || _detached) return 0;

        var failure = _entry.Failure;
        if (failure != null)
            throw new IOException("Shared stream failed", failure);

        // Wait for data if we've caught up to the write position
        while (_position >= _entry.WritePosition)
        {
            if (_entry.IsCompleted)
                return 0; // Stream finished naturally

            failure = _entry.Failure;
            if (failure != null)
                throw new IOException("Shared stream failed", failure);

            try
            {
                await _entry.WaitForDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        // Check if we've fallen behind the ring buffer window
        var validStart = _entry.ValidRangeStart;
        if (_position < validStart)
        {
            Log.Warning("[SharedStreamHandle] Reader detached — position {Position} behind valid range start {ValidStart}", _position, validStart);
            _detached = true;
            return 0;
        }

        // Read from ring buffer
        var bytesAvailable = _entry.WritePosition - _position;
        var bytesToCopy = (int)Math.Min(Math.Min(bytesAvailable, count), int.MaxValue);
        _entry.CopyFromRingBuffer(_position, buffer, offset, bytesToCopy);

        // Double-check: pump may have overwritten our bytes during the copy
        if (_position < _entry.ValidRangeStart)
        {
            Log.Warning("[SharedStreamHandle] Reader detached (post-copy race) — position {Position} behind valid range start {ValidStart}", _position, _entry.ValidRangeStart);
            _detached = true;
            return 0;
        }

        _position += bytesToCopy;

        // Report position to entry for backpressure tracking
        _entry.UpdateReaderPosition(_handleId, _position);

        return bytesToCopy;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Task.Run(() => ReadAsync(buffer, offset, count)).GetAwaiter().GetResult();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _entry.DetachReader(_handleId);
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _entry.DetachReader(_handleId);
        GC.SuppressFinalize(this);
    }
}
