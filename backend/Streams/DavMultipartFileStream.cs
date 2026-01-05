using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Streams;

public class DavMultipartFileStream(
    DavMultipartFile.FilePart[] fileParts,
    UsenetStreamingClient usenet,
    int concurrentConnections,
    ConnectionUsageContext? usageContext = null
) : Stream
{
    private CombinedStream? _innerStream;
    private bool _disposed;
    private readonly ConnectionUsageContext _usageContext = usageContext ?? new ConnectionUsageContext(ConnectionUsageType.Unknown);


    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_innerStream == null) _innerStream = GetCombinedStream();
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        // Initialize stream if not already created
        if (_innerStream == null) _innerStream = GetCombinedStream();

        // Use CombinedStream's built-in seeking
        return _innerStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        throw new InvalidOperationException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; } = fileParts.Select(x => x.FilePartByteRange.Count).Sum();

    public override long Position
    {
        get => _innerStream?.Position ?? 0;
        set => Seek(value, SeekOrigin.Begin);
    }

    private CombinedStream GetCombinedStream()
    {
        // Build list of stream factories with their lengths for seekable CombinedStream
        var parts = new List<(Func<Task<Stream>> StreamFactory, long Length)>();

        foreach (var filePart in fileParts)
        {
            var capturedPart = filePart; // Capture for closure
            parts.Add((
                StreamFactory: () =>
                {
                    // Use buffered streaming with stream caching for optimal performance
                    // CombinedStream now caches recently used parts to avoid re-creating buffers
                    var stream = usenet.GetFileStream(
                        capturedPart.SegmentIds,
                        capturedPart.SegmentIdByteRange.Count,
                        concurrentConnections,
                        _usageContext,
                        useBufferedStreaming: true
                    );
                    // Seek to the start of this part within the RAR file
                    stream.Seek(capturedPart.FilePartByteRange.StartInclusive, SeekOrigin.Begin);
                    // Limit the stream to only this part's length
                    return Task.FromResult(stream.LimitLength(capturedPart.FilePartByteRange.Count));
                },
                Length: capturedPart.FilePartByteRange.Count
            ));
        }

        return new CombinedStream(parts, maxCachedStreams: 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}