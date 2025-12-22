using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using Serilog;
using System.IO;

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
    private readonly ConnectionUsageContext? _usageContext;

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
        CancellationToken cancellationToken,
        ConnectionUsageContext? usageContext = null)
    {
        _usageContext = usageContext;
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
            _linkedCts.Token.SetScopedContext(usageContext ?? cancellationToken.GetContext<ConnectionUsageContext>())
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
                    var segmentCount = 0;
                    var currentSegmentId = "none";
                    var currentSegmentIndex = -1;
                    var stopwatch = new Stopwatch();

                    try
                    {
                        await foreach (var (index, segmentId) in segmentQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                        {
                            segmentCount++;
                            currentSegmentId = segmentId;
                            currentSegmentIndex = index;
                            stopwatch.Restart();

                            Stream? stream = null;
                            try
                            {
                                var fetchHeaders = index == 0;
                                var multiClient = GetMultiProviderClient(client);
                                if (multiClient != null)
                                {
                                    stream = await multiClient.GetBalancedSegmentStreamAsync(segmentId, fetchHeaders, ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    stream = await client.GetSegmentStreamAsync(segmentId, fetchHeaders, ct).ConfigureAwait(false);
                                }
                                
                                if (fetchHeaders && stream is YencHeaderStream yencStream && yencStream.ArticleHeaders != null)
                                {
                                    FileDate = yencStream.ArticleHeaders.Date;
                                    if (_usageContext?.DetailsObject != null)
                                    {
                                        _usageContext.Value.DetailsObject.FileDate = FileDate;
                                    }
                                }
                                
                                // Rent a buffer and read the segment into it
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

                                    var segmentData = new PooledSegmentData(segmentId, buffer, totalRead);

                                    // Store in dictionary temporarily
                                    fetchedSegments[index] = segmentData;
                                }
                                catch
                                {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                    throw;
                                }
                            }
                            finally
                            {
                                if (stream != null)
                                    await stream.DisposeAsync().ConfigureAwait(false);
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
                    }
                    catch (OperationCanceledException ex)
                    {
                        stopwatch.Stop();
                        if (ct.IsCancellationRequested)
                        {
                            Log.Warning($"[BufferedStream] Worker {workerId} canceled after processing {segmentCount} segments. " +
                                $"Segment: {currentSegmentIndex} ({currentSegmentId}). " +
                                $"Elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s. " +
                                $"Msg: {ex.Message}");
                        }
                        else
                        {
                            Log.Warning($"[BufferedStream] Worker {workerId} timed out after processing {segmentCount} segments. " +
                                $"Segment: {currentSegmentIndex} ({currentSegmentId}). " +
                                $"Elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s. " +
                                $"Msg: {ex.Message}");
                        }
                        throw;
                    }
                    catch (TimeoutException ex)
                    {
                        // Extract provider info from exception message (format: "... on provider hostname")
                        var providerInfo = ex.Message.Contains(" on provider ")
                            ? ex.Message.Substring(ex.Message.LastIndexOf(" on provider ") + 13)
                            : "unknown";
                        Log.Warning($"[BufferedStream] Worker {workerId} timed out after processing {segmentCount} segments (operation: GetSegmentStream, provider: {providerInfo})");
                        throw;
                    }
                    catch (UsenetArticleNotFoundException)
                    {
                        // Do not log error here. This is an expected condition when an article is missing.
                        // The exception will bubble up to FetchSegmentsAsync where it is handled gracefully.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BufferedStream] Worker {workerId} encountered error after processing {segmentCount} segments: {ex.GetType().Name} - {ex.Message}");
                        throw;
                    }
                })
                .ToList();

            // Wait for all workers to complete
            var workerCompletionTask = Task.WhenAll(workers);
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2), ct);
            var completedTask = await Task.WhenAny(workerCompletionTask, timeoutTask).ConfigureAwait(false);

            if (completedTask == timeoutTask)
            {
                Log.Warning($"[BufferedStream] Workers have not completed after 2 minutes. Still waiting...");
                await workerCompletionTask.ConfigureAwait(false);
            }

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
            _bufferChannel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            var jobName = (_usageContext.HasValue && _usageContext.Value.DetailsObject?.Text != null) 
                ? Path.GetFileName(_usageContext.Value.DetailsObject.Text) 
                : "Unknown";

            if (ex is UsenetArticleNotFoundException || (ex is AggregateException agg && agg.InnerExceptions.Any(e => e is UsenetArticleNotFoundException)))
            {
                // Log without stack trace for expected missing article errors
                Log.Error($"[BufferedStream] Error in FetchSegmentsAsync for Job: '{jobName}': {ex.Message}");
            }
            else if (ex is TimeoutException || (ex is AggregateException aggTimeout && aggTimeout.InnerExceptions.Any(e => e is TimeoutException)))
            {
                // Log without stack trace for timeouts (common with unreliable providers)
                Log.Warning($"[BufferedStream] Timeout in FetchSegmentsAsync for Job: '{jobName}': {ex.Message}");
            }
            else
            {
                Log.Error(ex, "[BufferedStream] Error in FetchSegmentsAsync for Job: '{JobName}'", jobName);
            }
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
                    {
                        break; // No more segments
                    }

                    if (!_bufferChannel.Reader.TryRead(out _currentSegment))
                    {
                        break;
                    }
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
    
    /// <summary>
    /// The date of the file on the Usenet server, populated when the first segment is fetched.
    /// </summary>
    public DateTimeOffset? FileDate { get; private set; }

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

    private static MultiProviderNntpClient? GetMultiProviderClient(INntpClient client)
    {
        while (true)
        {
            if (client is MultiProviderNntpClient multiProviderClient) return multiProviderClient;
            if (client is WrappingNntpClient wrappingClient)
            {
                client = wrappingClient.InnerClient;
                continue;
            }
            return null;
        }
    }
}
