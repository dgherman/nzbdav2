using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using Serilog;
using System.IO;
using System.Collections.Concurrent;
using NzbWebDAV.Database;

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
    private readonly INntpClient _client;

    private PooledSegmentData? _currentSegment;
    private int _currentSegmentPosition;
    private long _position;
    private bool _disposed;
    
    private int _totalFetchedCount;
    private int _totalReadCount;
    private int _bufferedCount; // Number of segments currently in memory buffer
    
    private int _nextIndexToRead = 0;
    private int _maxFetchedIndex = -1;
    private readonly int _totalSegments;

    public int BufferedCount => _bufferedCount;

    // Track corrupted segments for health check triggering
    private readonly ConcurrentBag<(int Index, string SegmentId)> _corruptedSegments = new();
    private readonly long[]? _segmentSizes;

    public BufferedSegmentStream(
        string[] segmentIds,
        long fileSize,
        INntpClient client,
        int concurrentConnections,
        int bufferSegmentCount,
        CancellationToken cancellationToken,
        ConnectionUsageContext? usageContext = null,
        long[]? segmentSizes = null)
    {
        _usageContext = usageContext;
        _segmentSizes = segmentSizes;
        _client = client;
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

        // Start background reporter
        if (_usageContext != null)
        {
            _ = Task.Run(async () => {
                try {
                    while (!contextToken.IsCancellationRequested) {
                        await Task.Delay(1000, contextToken).ConfigureAwait(false);
                        UpdateUsageContext();
                    }
                } catch {}
            }, contextToken);
        }

        Length = fileSize;
        _totalSegments = segmentIds.Length;
    }

    private void UpdateUsageContext()
    {
        if (_usageContext?.DetailsObject == null) return;
        
        // Update the shared details object
        var details = _usageContext.Value.DetailsObject;
        details.BufferedCount = _bufferedCount;
        details.BufferWindowStart = _nextIndexToRead;
        details.BufferWindowEnd = Math.Max(_nextIndexToRead, _maxFetchedIndex);
        details.TotalSegments = _totalSegments;

        // Occasionally trigger a UI update via ConnectionPool if possible
        var multiClient = GetMultiProviderClient(_client);
        if (multiClient != null)
        {
            foreach (var provider in multiClient.Providers)
            {
                provider.ConnectionPool.TriggerStatsUpdate();
            }
        }
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
            // Priority channel for racing stragglers or retrying preempted segments
            var urgentChannel = Channel.CreateUnbounded<(int index, string segmentId)>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false // Multiple sources (monitor, workers)
            });

            // Standard queue
            var segmentQueue = Channel.CreateBounded<(int index, string segmentId)>(new BoundedChannelOptions(bufferSegmentCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            // Track active assignments: Index -> (StartTime, Cts, WorkerId)
            var activeAssignments = new ConcurrentDictionary<int, (DateTimeOffset StartTime, CancellationTokenSource Cts, int WorkerId)>();
            
            // Track which segments are currently being raced to avoid double-racing
            var racingIndices = new ConcurrentDictionary<int, bool>();

            // Producer: Queue all segment IDs
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

            // Straggler Monitor Task
            var monitorTask = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false); // Check every 500ms

                        var nextNeeded = _nextIndexToRead;
                        
                        // Check if the next needed segment is active and running too long
                        if (activeAssignments.TryGetValue(nextNeeded, out var assignment))
                        {
                            var duration = DateTimeOffset.UtcNow - assignment.StartTime;
                            
                            // If running > 1.5s (or significantly slower than peers) and not already racing
                            if (duration.TotalSeconds > 1.5 && !racingIndices.ContainsKey(nextNeeded))
                            {
                                // Find a victim to preempt (highest index currently running)
                                var victim = activeAssignments.Keys.DefaultIfEmpty(-1).Max();
                                
                                // Only preempt if victim is significantly ahead (e.g. > 5 segments or > 2s ahead in stream)
                                // and not the same as nextNeeded
                                if (victim > nextNeeded + 5)
                                {
                                    if (activeAssignments.TryGetValue(victim, out var victimAssignment))
                                    {
                                        Log.Warning("[BufferedStream] STRAGGLER DETECTED: Segment {NextNeeded} running for {Duration}s. Preempting Segment {Victim} to race.", 
                                            nextNeeded, duration.TotalSeconds, victim);

                                        racingIndices.TryAdd(nextNeeded, true);
                                        
                                        // Cancel the victim to free up a worker
                                        try { victimAssignment.Cts.Cancel(); } catch {}

                                        // Re-queue victim (high priority to avoid starvation)
                                        _ = urgentChannel.Writer.WriteAsync((victim, segmentIds[victim]), ct);

                                        // Queue straggler race (high priority)
                                        _ = urgentChannel.Writer.WriteAsync((nextNeeded, segmentIds[nextNeeded]), ct);
                                    }
                                }
                                else if (activeAssignments.Count < concurrentConnections)
                                {
                                    // If we have spare capacity (unlikely if queue is full, but possible), just spawn race
                                    Log.Warning("[BufferedStream] STRAGGLER DETECTED: Segment {NextNeeded} running for {Duration}s. Spawning race (spare capacity).", 
                                            nextNeeded, duration.TotalSeconds);
                                    racingIndices.TryAdd(nextNeeded, true);
                                    _ = urgentChannel.Writer.WriteAsync((nextNeeded, segmentIds[nextNeeded]), ct);
                                }
                            }
                        }
                    }
                }
                catch { /* Ignore cancellation */ }
            }, ct);

            var fetchedSegments = new ConcurrentDictionary<int, PooledSegmentData>();
            var nextIndexToWrite = 0;
            var writeLock = new SemaphoreSlim(1, 1);

            // Consumers
            var workers = Enumerable.Range(0, concurrentConnections)
                .Select(async workerId =>
                {
                    // Reusable buffer for worker loop
                    while (!ct.IsCancellationRequested)
                    {
                        (int index, string segmentId) job;
                        bool isUrgent = false;

                        try
                        {
                            // Priority 1: Urgent Channel
                            if (urgentChannel.Reader.TryRead(out job))
                            {
                                isUrgent = true;
                            }
                            // Priority 2: Standard Queue
                            else
                            {
                                // Wait for data on either channel
                                // We prefer standard queue usually, unless urgent arrives.
                                // Since we can't easily wait on both, we wait on standard.
                                // If standard is empty, we wait. If urgent comes, we might miss it until standard has item?
                                // To fix this, we should really loop/delay or use a combined read.
                                // For simplicity: TryRead standard. If empty, WaitToReadAsync on both?
                                // Actually, producer fills standard fast. It's rarely empty unless EOF.
                                
                                if (!segmentQueue.Reader.TryRead(out job))
                                {
                                    // Wait for item available on EITHER
                                    var t1 = segmentQueue.Reader.WaitToReadAsync(ct).AsTask();
                                    var t2 = urgentChannel.Reader.WaitToReadAsync(ct).AsTask();
                                    
                                    await Task.WhenAny(t1, t2).ConfigureAwait(false);
                                    
                                    if (urgentChannel.Reader.TryRead(out job)) isUrgent = true;
                                    else if (!segmentQueue.Reader.TryRead(out job)) 
                                    {
                                        // Both empty or closed?
                                        if (segmentQueue.Reader.Completion.IsCompleted) break;
                                        continue; 
                                    }
                                }
                            }

                            // Skip if already fetched (race condition handling)
                            if (fetchedSegments.ContainsKey(job.index)) continue;

                            // Create job-specific CTS for preemption
                            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var assignment = (DateTimeOffset.UtcNow, jobCts, workerId);
                            
                            // Track assignment
                            // Note: If racing, multiple workers might have same index. We just overwrite or ignore.
                            // We mainly care about having *at least one* active.
                            activeAssignments[job.index] = assignment;

                            try
                            {
                                var segmentData = await FetchSegmentWithRetryAsync(job.index, job.segmentId, segmentIds, client, jobCts.Token).ConfigureAwait(false);

                                // Store result (first write wins)
                                if (fetchedSegments.TryAdd(job.index, segmentData))
                                {
                                    // Update max fetched
                                    lock (this) { if (job.index > _maxFetchedIndex) _maxFetchedIndex = job.index; }
                                    Interlocked.Increment(ref _bufferedCount);
                                    Interlocked.Increment(ref _totalFetchedCount);

                                    // Check write lock
                                    if (job.index == nextIndexToWrite || fetchedSegments.ContainsKey(nextIndexToWrite))
                                    {
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
                                else
                                {
                                    // Lost the race (already fetched), dispose data
                                    segmentData.Dispose();
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // If main CT not cancelled, we were preempted.
                                if (!ct.IsCancellationRequested)
                                {
                                    Log.Debug("[BufferedStream] Worker {WorkerId} preempted on segment {Index}.", workerId, job.index);
                                    // We DO NOT re-queue here; the monitor already re-queued it (or the race duplicate).
                                    // If we were the victim, monitor queued us. 
                                    // If we were the slow straggler being killed? We should probably re-queue just in case?
                                    // Monitor queued a duplicate. If we die, the duplicate runs. 
                                    // But monitor queues duplicate to URGENT.
                                    // If we were preempted, it means we were the VICTIM (high index).
                                    // Monitor re-queued us to URGENT. So we are fine.
                                }
                                else throw;
                            }
                            finally
                            {
                                activeAssignments.TryRemove(job.index, out _);
                                if (isUrgent && racingIndices.ContainsKey(job.index))
                                {
                                    // Remove from racing set if we finished (or failed)
                                    // But multiple might be racing. 
                                    // It's safe to remove; monitor checks "ContainsKey". 
                                    // If we finish, we don't need to race anymore.
                                    racingIndices.TryRemove(job.index, out _);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log and continue (FetchSegmentWithRetryAsync handles most errors, but just in case)
                            if (!ct.IsCancellationRequested)
                                Log.Error(ex, "[BufferedStream] Worker loop error");
                        }
                    }
                })
                .ToList();

            await Task.WhenAll(workers).ConfigureAwait(false);
            
            // Clean up
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Flush remaining
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
            Log.Error(ex, "[BufferedStream] Error in FetchSegmentsAsync");
            _bufferChannel.Writer.Complete(ex);
        }
    }

    /// <summary>
    /// Fetches a segment with retry logic for corruption/validation failures.
    /// Implements exponential backoff and graceful degradation.
    /// </summary>
    private async Task<PooledSegmentData> FetchSegmentWithRetryAsync(
        int index,
        string segmentId,
        string[] segmentIds,
        INntpClient client,
        CancellationToken ct)
    {
        const int maxRetries = 3;
        Exception? lastException = null;
        var jobName = _usageContext?.DetailsObject?.Text ?? "Unknown";

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            // Exponential backoff: 0s, 1s, 2s, 4s
            if (attempt > 0)
            {
                var delaySeconds = Math.Pow(2, attempt - 1);
                Log.Warning("[BufferedStream] RETRY: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Attempt={Attempt}/{MaxRetries}, Waiting {Delay}s before retry",
                    jobName, index, segmentIds.Length, segmentId, attempt + 1, maxRetries, delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }

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

                    // Validate segment size against YENC header
                    if (stream is YencHeaderStream yencHeaderStream)
                    {
                        var expectedSize = yencHeaderStream.Header.PartSize;
                        if (totalRead != expectedSize)
                        {
                            // If we got significantly less data than expected, this is likely a corrupted or incomplete segment
                            // Treat this as an error rather than just a warning
                            if (totalRead < expectedSize * 0.9) // Less than 90% of expected size
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                                throw new InvalidDataException(
                                    $"Incomplete segment for {jobName}: Segment {index}/{segmentIds.Length} ({segmentId}) " +
                                    $"expected {expectedSize} bytes but got only {totalRead} bytes ({totalRead - expectedSize} byte deficit). " +
                                    $"This may indicate a timeout, network issue, or corrupted segment."
                                );
                            }

                            Log.Warning("[BufferedStream] SEGMENT SIZE MISMATCH: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Expected={Expected} bytes, Got={Actual} bytes, Diff={Diff}",
                                jobName, index, segmentIds.Length, segmentId, expectedSize, totalRead, totalRead - expectedSize);
                        }
                    }

                    // Success! Return the segment
                    return new PooledSegmentData(segmentId, buffer, totalRead);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }
            }
            catch (InvalidDataException ex)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ex.Message, ex, ct);
                
                lastException = ex;
                Log.Warning("[BufferedStream] CORRUPTION DETECTED: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Attempt={Attempt}/{MaxRetries}: {Message}",
                    jobName, index, segmentIds.Length, segmentId, attempt + 1, maxRetries, ex.Message);

                // Will retry on next iteration
            }
            catch (UsenetArticleNotFoundException)
            {
                // Don't retry for missing articles - this is permanent
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ex.Message, ex, ct);

                lastException = ex;
                Log.Warning("[BufferedStream] ERROR FETCHING SEGMENT: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}), Attempt={Attempt}/{MaxRetries}: {Message}",
                    jobName, index, segmentIds.Length, segmentId, attempt + 1, maxRetries, ex.Message);
                // Will retry on next iteration
            }
            finally
            {
                if (stream != null)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        // All retries failed - use graceful degradation
        Log.Error("[BufferedStream] GRACEFUL DEGRADATION: Job={Job}, Segment={SegmentIndex}/{TotalSegments} (ID: {SegmentId}) failed after {MaxRetries} attempts. Substituting with zeros to allow stream to continue. Last error: {LastError}",
            jobName, index, segmentIds.Length, segmentId, maxRetries, lastException?.Message ?? "Unknown");

        // Track this segment as corrupted
        _corruptedSegments.Add((index, segmentId));

        // Report corruption back to database if we have a DavItemId
        if (_usageContext?.DetailsObject?.DavItemId != null)
        {
            var davItemId = _usageContext.Value.DetailsObject.DavItemId.Value;
            var reason = $"Data missing/corrupt after {maxRetries} retries: {lastException?.Message ?? "Unknown error"}";
            _ = Task.Run(async () => {
                try {
                    using var db = new DavDatabaseContext();
                    var item = await db.Items.FindAsync(davItemId);
                    if (item != null && !item.IsCorrupted) {
                        item.IsCorrupted = true;
                        item.CorruptionReason = reason;
                        await db.SaveChangesAsync();
                        Log.Information("[BufferedStream] Marked item {ItemId} as corrupted in database due to terminal segment failure.", davItemId);
                    }
                } catch (Exception dbEx) {
                    Log.Error(dbEx, "[BufferedStream] Failed to mark item as corrupted in database.");
                }
            });
        }

        // Determine correct size for zero-filling
        int zeroBufferSize;
        if (_segmentSizes != null && index < _segmentSizes.Length)
        {
            zeroBufferSize = (int)_segmentSizes[index];
        }
        else
        {
            // If we don't know the exact size, we cannot safely zero-fill without corrupting the file structure (shifting offsets).
            // It is safer to fail hard here.
            throw new InvalidDataException($"Cannot perform graceful degradation for segment {index} ({segmentId}) because segment size is unknown. Failing stream to prevent structural corruption.");
        }

        // Return a zero-filled segment of correct size
        var zeroBuffer = ArrayPool<byte>.Shared.Rent(zeroBufferSize);
        Array.Clear(zeroBuffer, 0, zeroBufferSize);
        return new PooledSegmentData(segmentId, zeroBuffer, zeroBufferSize);
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
                    // Update usage context while waiting to ensure UI shows we are waiting for next segment
                    UpdateUsageContext();

                    var waitWatch = Stopwatch.StartNew();
                    var hasData = await _bufferChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                    waitWatch.Stop();

                    if (waitWatch.ElapsedMilliseconds > 50)
                    {
                        Log.Warning("[BufferedStream] Starvation: Waited {Duration}ms for next segment (Buffered: {Buffered}, Fetched: {Fetched}, Read: {Read})", 
                            waitWatch.ElapsedMilliseconds, _bufferedCount, _totalFetchedCount, _totalReadCount);
                    }

                    if (!hasData)
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
                Interlocked.Decrement(ref _bufferedCount);
                Interlocked.Increment(ref _totalReadCount);
                Interlocked.Increment(ref _nextIndexToRead);
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

    /// <summary>
    /// List of segments that failed CRC/size validation after all retries.
    /// Used to trigger health checks for files with corruption.
    /// </summary>
    public IReadOnlyCollection<(int Index, string SegmentId)> CorruptedSegments => _corruptedSegments.ToList();

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
