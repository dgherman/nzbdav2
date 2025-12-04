using System.Buffers;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// High-performance buffered stream that maintains a read-ahead buffer of segments
/// for smooth, consistent streaming performance.
/// </summary>
public class BufferedSegmentStream : Stream
{
    private readonly Channel<PooledSegmentData> _bufferChannel;
    private readonly Task _fetchTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _linkedCts;
    private readonly IDisposable[] _contextScopes;

    private PooledSegmentData? _currentSegment;
    private int _currentSegmentPosition;
    private long _position;
    private bool _disposed;

    public BufferedSegmentStream(
        string[] segmentIds,
        long fileSize,
        INntpClient client,
        int concurrentConnections,
        int bufferSegmentCount,
        CancellationToken cancellationToken)
    {
        // Ensure buffer is large enough to prevent thrashing with high concurrency
        bufferSegmentCount = Math.Max(bufferSegmentCount, concurrentConnections * 5);

        // Create bounded channel for buffering
        var channelOptions = new BoundedChannelOptions(bufferSegmentCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };
        _bufferChannel = Channel.CreateBounded<PooledSegmentData>(channelOptions);

        // Link cancellation tokens and preserve context
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        // Copy all contexts from the original token to the linked token
        // Store context scopes so they live for the duration of the stream
        _contextScopes = new[]
        {
            _linkedCts.Token.SetScopedContext(cancellationToken.GetContext<LastSuccessfulProviderContext>()),
            _linkedCts.Token.SetScopedContext(cancellationToken.GetContext<ConnectionUsageContext>())
        };
        var contextToken = _linkedCts.Token;

        // Start background fetcher
        _fetchTask = Task.Run(async () =>
        {
            await FetchSegmentsAsync(segmentIds, client, concurrentConnections, bufferSegmentCount, contextToken)
                .ConfigureAwait(false);
        }, contextToken);

        Length = fileSize;
    }

    private async Task FetchSegmentsAsync(
        string[] segmentIds,
        INntpClient client,
        int concurrentConnections,
        int bufferSegmentCount,
        CancellationToken ct)
    {
        Log.Information($"[BufferedStream] Starting FetchSegmentsAsync for {segmentIds.Length} segments with {concurrentConnections} connections");
        try
        {
            // Use a producer-consumer pattern with indexed segments to maintain order
            // This is critical for video playback - segments MUST be in correct order
            var segmentQueue = Channel.CreateBounded<(int index, string segmentId)>(new BoundedChannelOptions(bufferSegmentCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            // Producer: Queue all segment IDs with their index
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < segmentIds.Length; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        await segmentQueue.Writer.WriteAsync((i, segmentIds[i]), ct).ConfigureAwait(false);
                    }
                    segmentQueue.Writer.Complete();
                }
                catch (Exception ex)
                {
                    segmentQueue.Writer.Complete(ex);
                }
            }, ct);

            // Use a concurrent dictionary to store results temporarily
            var fetchedSegments = new System.Collections.Concurrent.ConcurrentDictionary<int, PooledSegmentData>();
            var nextIndexToWrite = 0;
            var writeLock = new SemaphoreSlim(1, 1);

            // Consumers: Create exactly N worker tasks
            var workers = Enumerable.Range(0, concurrentConnections)
                .Select(async workerId =>
                {
                    Log.Debug($"[BufferedStream] Worker {workerId} started");
                    await foreach (var (index, segmentId) in segmentQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        var stream = await client.GetSegmentStreamAsync(segmentId, false, ct)
                            .ConfigureAwait(false);

                        // Rent a buffer and read the segment into it
                        // We need to read the entire stream, so we use a MemoryStream as an intermediate
                        // if we don't know the size, OR if we do know the size, we can rent directly.
                        // Assuming average segment size is reasonable (e.g. 700KB), but it can vary.
                        // Since we need to store it in 'PooledSegmentData', let's read fully.
                        // To avoid allocating a new array, we can use a rented buffer, 
                        // but we need to know the length.
                        // Typically Usenet segments are a known size, but here we just have a stream.
                        // Let's read into a rented buffer, resizing if necessary (or just use a large enough initial buffer).
                        // A safer approach for unknown stream length without re-allocation is tricky with just one buffer.
                        // However, typical segments are < 1MB. 
                        
                        // Let's use a recyclable memory stream approach or just simple array resizing manually with pool.
                        // For simplicity and performance, let's assume a reasonable max segment size 
                        // or just read into a temporary memory stream and THEN copy to a rented array of exact size?
                        // No, that defeats the purpose. 
                        
                        // Better approach: Use the stream length if available? 
                        // The underlying stream from UsenetClient likely doesn't support Length.
                        
                        // Let's buffer into a rented array. Start with 1MB (typical max article size is often 700KB-1MB).
                        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024); 
                        var totalRead = 0;
                        
                        try 
                        {
                            while (true)
                            {
                                if (totalRead == buffer.Length)
                                {
                                    // Resize
                                    var newBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                                    Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalRead);
                                    ArrayPool<byte>.Shared.Return(buffer);
                                    buffer = newBuffer;
                                }
                                
                                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
                                if (read == 0) break;
                                totalRead += read;
                            }
                            
                            await stream.DisposeAsync().ConfigureAwait(false);

                            var segmentData = new PooledSegmentData(segmentId, buffer, totalRead);

                            // Store in dictionary temporarily
                            fetchedSegments[index] = segmentData;
                        }
                        catch
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            throw;
                        }

                        // Try to write any consecutive segments to the buffer channel in order
                        await writeLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            while (fetchedSegments.TryRemove(nextIndexToWrite, out var orderedSegment))
                            {
                                await _bufferChannel.Writer.WriteAsync(orderedSegment, ct).ConfigureAwait(false);
                                nextIndexToWrite++;
                            }
                        }
                        finally
                        {
                            writeLock.Release();
                        }
                    }
                })
                .ToList();

            // Wait for all workers to complete
            await Task.WhenAll(workers).ConfigureAwait(false);

            // Ensure all segments were written (shouldn't have any left)
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (fetchedSegments.TryRemove(nextIndexToWrite, out var orderedSegment))
                {
                    await _bufferChannel.Writer.WriteAsync(orderedSegment, ct).ConfigureAwait(false);
                    nextIndexToWrite++;
                }
            }
            finally
            {
                writeLock.Release();
                writeLock.Dispose();
            }

            _bufferChannel.Writer.Complete();
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[BufferedStream] FetchSegmentsAsync canceled");
            _bufferChannel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BufferedStream] Error in FetchSegmentsAsync");
            _bufferChannel.Writer.Complete(ex);
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0) return 0;

        int totalRead = 0;

        while (totalRead < count && !cancellationToken.IsCancellationRequested)
        {
            // Get current segment if we don't have one
            if (_currentSegment == null)
            {
                if (!_bufferChannel.Reader.TryRead(out _currentSegment))
                {
                    if (!await _bufferChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        break; // No more segments

                    if (!_bufferChannel.Reader.TryRead(out _currentSegment))
                        break;
                }

                _currentSegmentPosition = 0;
            }

            // Read from current segment
            var bytesAvailable = _currentSegment.Length - _currentSegmentPosition;
            if (bytesAvailable == 0)
            {
                _currentSegment.Dispose();
                _currentSegment = null;
                continue;
            }

            var bytesToRead = Math.Min(count - totalRead, bytesAvailable);
            Buffer.BlockCopy(_currentSegment.Data, _currentSegmentPosition, buffer, offset + totalRead, bytesToRead);

            _currentSegmentPosition += bytesToRead;
            totalRead += bytesToRead;
            _position += bytesToRead;

            // If segment is exhausted, move to next
            if (_currentSegmentPosition >= _currentSegment.Length)
            {
                _currentSegment.Dispose();
                _currentSegment = null;
            }
        }

        return totalRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("Seeking is not supported.");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException("Seeking is not supported.");
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            _bufferChannel.Writer.TryComplete();
            try { _fetchTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
            
            _currentSegment?.Dispose();
            _currentSegment = null;

            // Dispose context scopes
            foreach (var scope in _contextScopes)
                scope?.Dispose();

            _linkedCts.Dispose();
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _cts.Cancel();
        _bufferChannel.Writer.TryComplete();

        try
        {
            await _fetchTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch { }

        _cts.Dispose();
        
        _currentSegment?.Dispose();
        _currentSegment = null;

        // Dispose context scopes
        foreach (var scope in _contextScopes)
            scope?.Dispose();

        _linkedCts.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private class PooledSegmentData : IDisposable
    {
        private byte[]? _buffer;
        
        public string SegmentId { get; }
        public byte[] Data => _buffer ?? Array.Empty<byte>();
        public int Length { get; }

        public PooledSegmentData(string segmentId, byte[] buffer, int length)
        {
            SegmentId = segmentId;
            _buffer = buffer;
            Length = length;
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }
}
