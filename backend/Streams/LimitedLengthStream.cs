using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams;

public class LimitedLengthStream(Stream stream, long length, bool leaveOpen = false) : Stream
{
    private bool _disposed;
    private long _position = 0;
    private readonly long _basePosition = stream.CanSeek ? stream.Position : 0;

    public override void Flush() => stream.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, SigtermUtil.GetCancellationToken()).GetAwaiter().GetResult();

    public override async Task<int>
        ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // If we've already read the specified length, return 0 (end of stream)
        if (_position >= length)
            return 0;

        // Calculate how many bytes we can still read
        var remainingBytes = length - _position;
        var bytesToRead = (int)Math.Min(remainingBytes, buffer.Length);

        // Read from the underlying stream
        var bytesRead = await stream.ReadAsync(buffer[..bytesToRead], cancellationToken).ConfigureAwait(false);

        // Update the position by the number of bytes read
        _position += bytesRead;

        // Return the number of bytes read
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!CanSeek) throw new NotSupportedException();

        var targetPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
        };

        targetPosition = Math.Clamp(targetPosition, 0, length);
        stream.Seek(_basePosition + targetPosition, SeekOrigin.Begin);
        _position = targetPosition;
        return _position;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!leaveOpen)
        {
            stream.Dispose();
        }
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (!leaveOpen)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}