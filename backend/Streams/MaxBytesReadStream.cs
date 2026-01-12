namespace NzbWebDAV.Streams;

public class MaxBytesReadStream(Stream stream, long maxBytes, bool leaveOpen = false) : Stream
{
    private long _totalBytesRead = 0;
    private readonly long _maxPosition = maxBytes; // Limit seek position to prevent hanging on far seeks

    public override void Flush() => stream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = stream.Read(buffer, offset, count);
        CheckLimit(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        CheckLimit(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        CheckLimit(read);
        return read;
    }

    private void CheckLimit(int bytesRead)
    {
        if (bytesRead <= 0) return;
        _totalBytesRead += bytesRead;
        if (_totalBytesRead > maxBytes)
        {
            throw new IOException($"Read limit of {maxBytes} bytes exceeded while parsing headers.");
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = stream.Seek(offset, origin);

        // Enforce position limit - if seeking beyond max, fail fast to prevent hanging on reads
        if (newPosition > _maxPosition)
        {
            throw new IOException($"Seek position {newPosition} exceeds maximum allowed position of {_maxPosition} bytes. " +
                "This usually indicates a corrupt RAR archive or multi-volume RAR with headers beyond the readable range.");
        }

        return newPosition;
    }
    public override void SetLength(long value) => stream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => stream.CanWrite;
    public override long Length => stream.Length;

    public override long Position
    {
        get => stream.Position;
        set => stream.Position = value;
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            stream.Dispose();
        }
    }
}
