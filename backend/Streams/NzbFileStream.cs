using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams;

public class NzbFileStream : Stream
{
    private readonly string[] _fileSegmentIds;
    private readonly long _fileSize;
    private readonly INntpClient _client;
    private readonly int _concurrentConnections;
    private readonly bool _useBufferedStreaming;
    private readonly int _bufferSize;
    private readonly long[]? _segmentOffsets; // Cumulative offsets for instant seeking

    private long _position = 0;
    private CombinedStream? _innerStream;
    private bool _disposed;
    private readonly ConnectionUsageContext _usageContext;
    private readonly CancellationTokenSource _streamCts;
    private IDisposable? _contextScope;
    private CancellationTokenRegistration _cancellationRegistration;

    // Infinite loop detection
    private long _lastSeekOffset = -1;
    private int _consecutiveSeeksToSameOffset = 0;
    private long _totalSeekCount = 0;
    private long _totalReadCount = 0;

    public NzbFileStream(
        string[] fileSegmentIds,
        long fileSize,
        INntpClient client,
        int concurrentConnections,
        ConnectionUsageContext? usageContext = null,
        bool useBufferedStreaming = true,
        int bufferSize = 20,  // Increased from 10 for better read-ahead buffering
        long[]? segmentSizes = null
    )
    {
        _usageContext = usageContext ?? new ConnectionUsageContext(ConnectionUsageType.Unknown);
        Serilog.Log.Debug("[NzbFileStream] Initializing stream (Size: {FileSize} bytes, Segments: {SegmentCount}, Context: {UsageContext})", fileSize, fileSegmentIds.Length, _usageContext.UsageType);
        
        _fileSegmentIds = fileSegmentIds;
        _fileSize = fileSize;
        _client = client;
        _concurrentConnections = concurrentConnections;
        _useBufferedStreaming = useBufferedStreaming;
        _bufferSize = bufferSize;
        _streamCts = new CancellationTokenSource();

        if (segmentSizes != null && segmentSizes.Length == fileSegmentIds.Length)
        {
            _segmentOffsets = new long[segmentSizes.Length + 1];
            long current = 0;
            for (int i = 0; i < segmentSizes.Length; i++)
            {
                _segmentOffsets[i] = current;
                current += segmentSizes[i];
            }
            _segmentOffsets[segmentSizes.Length] = current;
            
            // Validate cached size matches expected size
            if (current != _fileSize)
            {
                Serilog.Log.Warning("[NzbFileStream] Cached segment sizes total {CachedSize} but expected {FileSize}. Ignoring cache.", current, _fileSize);
                _segmentOffsets = null;
            }
        }
    }

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        _totalReadCount++;

        if (_innerStream == null)
        {
            Serilog.Log.Debug("[NzbFileStream] Creating inner stream at position {Position}", _position);
            _innerStream = await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        }

        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _position += read;

        // Reset consecutive seek counter on successful read
        if (read > 0)
        {
            _consecutiveSeeksToSameOffset = 0;
        }

        if (_totalReadCount % 100 == 0)
        {
            Serilog.Log.Debug("[NzbFileStream] Progress: {TotalReads} reads, {TotalSeeks} seeks. Current position: {Position}/{FileSize}",
                _totalReadCount, _totalSeekCount, _position, _fileSize);
        }

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _totalSeekCount++;
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");

        // Detect infinite loop - seeking to the same offset repeatedly without reads
        if (absoluteOffset == _lastSeekOffset)
        {
            _consecutiveSeeksToSameOffset++;
            if (_consecutiveSeeksToSameOffset > 100)
            {
                Serilog.Log.Error("[NzbFileStream] INFINITE SEEK LOOP DETECTED! Seeked to offset {Offset} {Count} times consecutively. Total seeks: {TotalSeeks}, Total reads: {TotalReads}",
                    absoluteOffset, _consecutiveSeeksToSameOffset, _totalSeekCount, _totalReadCount);
                throw new InvalidOperationException($"Infinite seek loop detected: offset {absoluteOffset} seeked {_consecutiveSeeksToSameOffset} times without reads");
            }
            else if (_consecutiveSeeksToSameOffset % 10 == 0)
            {
                Serilog.Log.Warning("[NzbFileStream] Repeated seeks to same offset {Offset}. Count: {Count}, Total seeks: {TotalSeeks}, Total reads: {TotalReads}",
                    absoluteOffset, _consecutiveSeeksToSameOffset, _totalSeekCount, _totalReadCount);
            }
        }
        else
        {
            _lastSeekOffset = absoluteOffset;
            _consecutiveSeeksToSameOffset = 1;
        }

        Serilog.Log.Debug("[NzbFileStream] Seek #{SeekNum} to offset: {Offset}, origin: {Origin}. Current position: {CurrentPosition}",
            _totalSeekCount, offset, origin, _position);

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
    public override long Length => _fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }


    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        if (_segmentOffsets != null)
        {
            // Binary search for the segment index
            // We want index i such that _segmentOffsets[i] <= byteOffset < _segmentOffsets[i+1]
            var index = Array.BinarySearch(_segmentOffsets, byteOffset);
            if (index < 0)
            {
                // If not found, BinarySearch returns the bitwise complement of the index of the next element that is larger
                index = ~index - 1;
            }

            if (index >= 0 && index < _fileSegmentIds.Length)
            {
                Serilog.Log.Debug("[NzbFileStream] Cache hit for offset {Offset}: segment index {Index}", byteOffset, index);
                return new InterpolationSearch.Result(index, new LongRange(_segmentOffsets[index], _segmentOffsets[index + 1]));
            }
        }

        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, _fileSegmentIds.Length),
            new LongRange(0, _fileSize),
            async (guess) =>
            {
                var header = await _client.GetSegmentYencHeaderAsync(_fileSegmentIds[guess], ct).ConfigureAwait(false);
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
            var bytesToDiscard = rangeStart - foundSegment.FoundByteRange.StartInclusive;
            if (bytesToDiscard > 0)
            {
                if (stream.CanSeek)
                {
                    stream.Seek(bytesToDiscard, SeekOrigin.Current);
                }
                else
                {
                    await stream.DiscardBytesAsync(bytesToDiscard).ConfigureAwait(false);
                }
            }
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
        // Dispose previous registration to prevent leak
        _cancellationRegistration.Dispose();

        // Dispose previous context scope if any
        _contextScope?.Dispose();

        // No need to copy ReservedPooledConnectionsContext - operation limits handle this now

        // Disable buffered streaming for Queue processing since it only reads small amounts
        // (e.g., just the first segment for file size detection)
        var shouldUseBufferedStreaming = _useBufferedStreaming &&
            _usageContext.UsageType != ConnectionUsageType.Queue;

        // Use buffered streaming if configured for better performance
        if (shouldUseBufferedStreaming && _concurrentConnections >= 3 && _fileSegmentIds.Length > _concurrentConnections)
        {
            // Set BufferedStreaming context - this will be the ONLY ConnectionUsageContext
            var detailsObj = new ConnectionUsageDetails
            {
                Text = _usageContext.Details ?? "",
                JobName = _usageContext.DetailsObject?.JobName,
                AffinityKey = _usageContext.DetailsObject?.AffinityKey,
                DavItemId = _usageContext.DetailsObject?.DavItemId,
                FileDate = _usageContext.DetailsObject?.FileDate
            };
            var bufferedContext = new ConnectionUsageContext(
                ConnectionUsageType.BufferedStreaming,
                detailsObj
            );
            
            var remainingSegments = _fileSegmentIds[firstSegmentIndex..];
            var remainingSize = _segmentOffsets != null 
                ? _fileSize - _segmentOffsets[firstSegmentIndex]
                : _fileSize - firstSegmentIndex * (_fileSize / _fileSegmentIds.Length);

            long[]? remainingSegmentSizes = null;
            if (_segmentOffsets != null)
            {
                remainingSegmentSizes = new long[remainingSegments.Length];
                for (int i = 0; i < remainingSegments.Length; i++)
                {
                    int originalIndex = firstSegmentIndex + i;
                    if (originalIndex + 1 < _segmentOffsets.Length)
                    {
                        remainingSegmentSizes[i] = _segmentOffsets[originalIndex + 1] - _segmentOffsets[originalIndex];
                    }
                }
            }

            Serilog.Log.Debug("[NzbFileStream] Creating BufferedSegmentStream for {SegmentCount} segments, approximated size: {ApproximateSize}, concurrent connections: {ConcurrentConnections}, buffer size: {BufferSize}",
                remainingSegments.Length, remainingSize, _concurrentConnections, _bufferSize);
            _contextScope = _streamCts.Token.SetScopedContext(bufferedContext);
            var bufferedContextCt = _streamCts.Token;
            var bufferedStream = new BufferedSegmentStream(
                remainingSegments,
                remainingSize, // Use exact size if available
                _client,
                _concurrentConnections,
                _bufferSize,
                bufferedContextCt,
                bufferedContext,
                remainingSegmentSizes
            );

            // Link cancellation from parent to child manually (one-way, doesn't copy contexts)
            // Safe cancellation: only cancel if not already disposed
            _cancellationRegistration = ct.Register(() =>
            {
                if (!_disposed)
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
            if (!_disposed)
            {
                try { _streamCts.Cancel(); } catch (ObjectDisposedException) { }
            }
        });

        // Use Lazy CombinedStream to avoid scheduling all tasks at once
        var parts = new List<(Func<Task<Stream>>, long)>();
        for (var i = firstSegmentIndex; i < _fileSegmentIds.Length; i++)
        {
            var segmentId = _fileSegmentIds[i];
            var size = _segmentOffsets != null && i + 1 < _segmentOffsets.Length
                ? (_segmentOffsets[i + 1] - _segmentOffsets[i])
                : -1L;

            // Capture loop variable
            var capturedId = segmentId;
            parts.Add((async () => (Stream)await _client.GetSegmentStreamAsync(capturedId, false, contextCt).ConfigureAwait(false), size));
        }

        // Disable caching to prevent permit exhaustion (especially for Queue processing)
        return new CombinedStream(parts, maxCachedStreams: 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        Serilog.Log.Debug("[NzbFileStream] Disposing NzbFileStream. Disposing: {Disposing}", disposing);
        _disposed = true;
        _cancellationRegistration.Dispose(); // Unregister callback first
        _innerStream?.Dispose();
        _streamCts.Dispose();
        _contextScope?.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellationRegistration.Dispose(); // Unregister callback first
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _streamCts.Dispose();
        _contextScope?.Dispose();
        GC.SuppressFinalize(this);
    }
}