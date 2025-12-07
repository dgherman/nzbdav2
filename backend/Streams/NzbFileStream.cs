using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient client,
    int concurrentConnections,
    ConnectionUsageContext? usageContext = null,
    bool useBufferedStreaming = true,
    int bufferSize = 10
) : Stream
{
    private long _position = 0;
    private CombinedStream? _innerStream;
    private bool _disposed;
    private readonly ConnectionUsageContext _usageContext = usageContext ?? new ConnectionUsageContext(ConnectionUsageType.Unknown);
    private CancellationTokenSource? _streamCts;
    private IDisposable? _contextScope;
    private CancellationTokenRegistration _cancellationRegistration;

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
        if (_innerStream == null) _innerStream = await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Serilog.Log.Debug("[NzbFileStream] Seek called with offset: {Offset}, origin: {Origin}. Current position: {CurrentPosition}", offset, origin, _position);
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset)
        {
            Serilog.Log.Debug("[NzbFileStream] Seek resulted in no change. Position: {Position}", _position);
            return _position;
        }
        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        Serilog.Log.Debug("[NzbFileStream] Seek completed. New position: {NewPosition}", _position);
        return _position;
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
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }


    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                var header = await client.GetSegmentYencHeaderAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
            },
            ct
        ).ConfigureAwait(false);
    }

    private async Task<CombinedStream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0) return GetCombinedStream(0, cancellationToken);

        using var seekCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var _ = seekCts.Token.SetScopedContext(_usageContext);

        var foundSegment = await SeekSegment(rangeStart, seekCts.Token).ConfigureAwait(false);
        var stream = GetCombinedStream(foundSegment.FoundIndex, cancellationToken);
        try
        {
            await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private CombinedStream GetCombinedStream(int firstSegmentIndex, CancellationToken ct)
    {
        // Create a NEW cancellation token that will live for the stream's lifetime
        // IMPORTANT: We create a new CTS (not linked) to avoid inheriting the parent's ConnectionUsageContext
        // which would cause double-counting (e.g., Queue + BufferedStreaming for the same connections)
        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();

        // Dispose previous context scope if any
        _contextScope?.Dispose();

        // No need to copy ReservedPooledConnectionsContext - operation limits handle this now

        // Disable buffered streaming for Queue processing since it only reads small amounts
        // (e.g., just the first segment for file size detection)
        var shouldUseBufferedStreaming = useBufferedStreaming &&
            _usageContext.UsageType != ConnectionUsageType.Queue;

        // Use buffered streaming if configured for better performance
        if (shouldUseBufferedStreaming && concurrentConnections >= 3 && fileSegmentIds.Length > concurrentConnections)
        {
            // Set BufferedStreaming context - this will be the ONLY ConnectionUsageContext
            var detailsObj = new ConnectionUsageDetails { Text = _usageContext.Details ?? "" };
            var bufferedContext = new ConnectionUsageContext(
                ConnectionUsageType.BufferedStreaming,
                detailsObj
            );
            
            var remainingSegments = fileSegmentIds[firstSegmentIndex..];
            Serilog.Log.Debug("[NzbFileStream] Creating BufferedSegmentStream for {SegmentCount} segments, approximated size: {ApproximateSize}, concurrent connections: {ConcurrentConnections}, buffer size: {BufferSize}",
                remainingSegments.Length, fileSize - firstSegmentIndex * (fileSize / fileSegmentIds.Length), concurrentConnections, bufferSize);
            _contextScope = _streamCts.Token.SetScopedContext(bufferedContext);
            var bufferedContextCt = _streamCts.Token;
            var bufferedStream = new BufferedSegmentStream(
                remainingSegments,
                fileSize - firstSegmentIndex * (fileSize / fileSegmentIds.Length), // Approximate remaining size
                client,
                concurrentConnections,
                bufferSize,
                bufferedContextCt,
                bufferedContext
            );

            // Link cancellation from parent to child manually (one-way, doesn't copy contexts)
            // Safe cancellation: only cancel if not already disposed
            _cancellationRegistration = ct.Register(() =>
            {
                if (!_disposed && _streamCts != null)
                {
                    try { _streamCts.Cancel(); } catch (ObjectDisposedException) { }
                }
            });

            return new CombinedStream(new[] { Task.FromResult<Stream>(bufferedStream) });
        }

        // Fallback to original implementation for small files or low concurrency
        // Set context for non-buffered streaming and keep scope alive
        _contextScope = _streamCts.Token.SetScopedContext(_usageContext);
        var contextCt = _streamCts.Token;

        // Link cancellation from parent to child manually (one-way, doesn't copy contexts)
        // Safe cancellation: only cancel if not already disposed
        _cancellationRegistration = ct.Register(() =>
        {
            if (!_disposed && _streamCts != null)
            {
                try { _streamCts.Cancel(); } catch (ObjectDisposedException) { }
            }
        });

        return new CombinedStream(
            fileSegmentIds[firstSegmentIndex..]
                .Select(async x => (Stream)await client.GetSegmentStreamAsync(x, false, contextCt).ConfigureAwait(false))
                .WithConcurrency(concurrentConnections)
        );
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        Serilog.Log.Debug("[NzbFileStream] Disposing NzbFileStream. Disposing: {Disposing}", disposing);
        _disposed = true;
        _cancellationRegistration.Dispose(); // Unregister callback first
        _innerStream?.Dispose();
        _streamCts?.Dispose();
        _contextScope?.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Serilog.Log.Debug("[NzbFileStream] Disposing NzbFileStream asynchronously.");
        _disposed = true;
        _cancellationRegistration.Dispose(); // Unregister callback first
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _streamCts?.Dispose();
        _contextScope?.Dispose();
        GC.SuppressFinalize(this);
    }
}