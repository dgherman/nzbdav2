using System.Collections.Concurrent;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Owns a single BufferedSegmentStream and exposes its data through a ring buffer
/// to multiple SharedStreamHandle readers. Manages lifetime via reference counting
/// and a grace period timer. The pump applies backpressure — it will not overwrite
/// data that the slowest active reader hasn't consumed yet.
/// </summary>
public class SharedStreamEntry : IDisposable
{
    public enum EntryState { Active, GracePeriod, Failed, Disposed }

    private readonly byte[] _ringBuffer;
    private readonly int _ringBufferSize;
    private readonly long _basePosition; // Absolute byte offset of first byte written
    private readonly BufferedSegmentStream _innerStream;
    private readonly SemaphoreSlim _slot; // Acquired semaphore from TryAcquireSlot
    private readonly Guid _davItemId;
    private readonly int _gracePeriodSeconds;
    private readonly Action<Guid> _evictCallback; // Calls SharedStreamManager.Evict
    private readonly CancellationTokenSource _entryCts; // Entry-scoped cancellation, independent of any request
    private readonly IDisposable? _contextScope; // Entry-scoped cancellation-token context lifetime

    private long _writePosition; // Absolute byte offset of next write
    private int _readerCount;
    private volatile EntryState _state = EntryState.Active;
    private Exception? _failure;
    private volatile bool _completed; // Inner stream returned 0 (natural end)
    private Timer? _graceTimer;
    private Task? _pumpTask;

    // Per-reader position tracking for backpressure
    private readonly ConcurrentDictionary<int, long> _readerPositions = new();
    private int _nextHandleId;

    // Signaling: pump notifies ALL waiting readers when new data is available (broadcast)
    private TaskCompletionSource _dataAvailableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Signaling: readers notify pump when they advance (for backpressure release)
    private TaskCompletionSource _readerAdvancedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Gate: pauses pump when no readers are active
    private readonly ManualResetEventSlim _pumpGate = new(true); // Start open (first reader is attaching)

    private readonly object _stateLock = new();

    public long WritePosition => Volatile.Read(ref _writePosition);
    public long ValidRangeStart => Math.Max(_basePosition, WritePosition - _ringBufferSize);
    public bool IsCompleted => _completed;
    public Exception? Failure => _failure;
    public long StreamLength { get; }
    public EntryState State => _state;

    public SharedStreamEntry(
        BufferedSegmentStream innerStream,
        SemaphoreSlim slot,
        Guid davItemId,
        long basePosition,
        long streamLength,
        int ringBufferSize,
        int gracePeriodSeconds,
        Action<Guid> evictCallback,
        CancellationTokenSource entryCts,
        IDisposable? contextScope = null)
    {
        _innerStream = innerStream;
        _slot = slot;
        _davItemId = davItemId;
        _basePosition = basePosition;
        _writePosition = basePosition;
        StreamLength = streamLength;
        _ringBufferSize = ringBufferSize;
        _ringBuffer = new byte[ringBufferSize];
        _gracePeriodSeconds = gracePeriodSeconds;
        _evictCallback = evictCallback;
        _entryCts = entryCts;
        _contextScope = contextScope;
        _readerCount = 1; // First reader is being created by the caller
    }

    /// <summary>
    /// Start the background pump task. Must be called after construction.
    /// </summary>
    public void StartPump()
    {
        _pumpTask = Task.Run(PumpLoop);
    }

    /// <summary>
    /// Register a reader's position for backpressure tracking.
    /// Returns a handle ID that must be passed to UpdateReaderPosition and UnregisterReader.
    /// </summary>
    internal int RegisterReader(long position)
    {
        var id = Interlocked.Increment(ref _nextHandleId);
        _readerPositions[id] = position;
        return id;
    }

    /// <summary>
    /// Update a reader's current position. Called by SharedStreamHandle after each read.
    /// Signals the pump in case it was waiting on backpressure.
    /// </summary>
    internal void UpdateReaderPosition(int handleId, long position)
    {
        _readerPositions[handleId] = position;
        // Signal pump that a reader advanced (may release backpressure)
        Interlocked.Exchange(ref _readerAdvancedTcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Remove a reader from position tracking. Called by SharedStreamHandle on dispose.
    /// </summary>
    internal void UnregisterReader(int handleId)
    {
        _readerPositions.TryRemove(handleId, out _);
        // Signal pump — removing the slowest reader may release backpressure
        Interlocked.Exchange(ref _readerAdvancedTcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Get the position of the slowest active reader, or null if no readers are tracked.
    /// </summary>
    private long? SlowestReaderPosition
    {
        get
        {
            long? min = null;
            foreach (var kvp in _readerPositions)
            {
                if (min == null || kvp.Value < min.Value)
                    min = kvp.Value;
            }
            return min;
        }
    }

    /// <summary>
    /// Copy bytes from the ring buffer into the caller's buffer.
    /// Caller must ensure position is within the valid range.
    /// </summary>
    public void CopyFromRingBuffer(long absolutePosition, byte[] destination, int destOffset, int count)
    {
        var ringOffset = (int)(absolutePosition % _ringBufferSize);
        var firstChunk = Math.Min(count, _ringBufferSize - ringOffset);
        Buffer.BlockCopy(_ringBuffer, ringOffset, destination, destOffset, firstChunk);
        if (firstChunk < count)
        {
            // Wrap around
            Buffer.BlockCopy(_ringBuffer, 0, destination, destOffset + firstChunk, count - firstChunk);
        }
    }

    /// <summary>
    /// Wait for the pump to write new data. Returns when data is available or entry fails.
    /// </summary>
    public async Task WaitForDataAsync(CancellationToken ct)
    {
        await Volatile.Read(ref _dataAvailableTcs).Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Attach a new reader. Returns a SharedStreamHandle if the position is within range, null otherwise.
    /// </summary>
    public SharedStreamHandle? TryAttachReader(long startPosition)
    {
        lock (_stateLock)
        {
            if (_state == EntryState.Disposed || _state == EntryState.Failed)
                return null;

            // Position must be >= basePosition and within the ring buffer window
            if (startPosition < _basePosition)
                return null;

            var validStart = ValidRangeStart;
            if (startPosition < validStart)
                return null;

            // Position can be at or ahead of write position (reader will wait for pump)
            // But don't let a reader attach too far ahead — they'd wait forever
            // Allow up to writePosition (they'll wait for pump to catch up)
            // Don't allow beyond stream length
            if (startPosition > _basePosition + StreamLength)
                return null;

            Interlocked.Increment(ref _readerCount);
            var handleId = RegisterReader(startPosition);

            // Cancel grace timer if we're in grace period
            if (_state == EntryState.GracePeriod)
            {
                _graceTimer?.Dispose();
                _graceTimer = null;
                _state = EntryState.Active;
                _pumpGate.Set(); // Resume pump
                Log.Debug("[SharedStreamEntry] Reader attached during grace period, resuming pump. DavItemId={DavItemId}", _davItemId);
            }

            return new SharedStreamHandle(this, startPosition, handleId);
        }
    }

    /// <summary>
    /// Called by SharedStreamHandle.Dispose when a reader disconnects.
    /// </summary>
    internal void DetachReader(int handleId)
    {
        UnregisterReader(handleId);

        var remaining = Interlocked.Decrement(ref _readerCount);
        if (remaining > 0) return;

        lock (_stateLock)
        {
            if (_state != EntryState.Active) return;

            _state = EntryState.GracePeriod;
            _pumpGate.Reset(); // Pause pump
            Log.Debug("[SharedStreamEntry] Last reader detached, starting grace period ({GracePeriod}s). DavItemId={DavItemId}", _gracePeriodSeconds, _davItemId);

            _graceTimer = new Timer(_ => OnGracePeriodExpired(), null,
                TimeSpan.FromSeconds(_gracePeriodSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnGracePeriodExpired()
    {
        bool shouldCleanup;
        lock (_stateLock)
        {
            if (_state != EntryState.GracePeriod) return;
            Log.Debug("[SharedStreamEntry] Grace period expired, disposing. DavItemId={DavItemId}", _davItemId);
            shouldCleanup = SetDisposedState();
        }

        if (shouldCleanup)
        {
            _evictCallback(_davItemId);
            CleanupResources();
        }
    }

    private async Task PumpLoop()
    {
        var buffer = new byte[256 * 1024]; // 256KB chunks

        try
        {
            while (true)
            {
                // Block if no readers are active (grace period)
                _pumpGate.Wait();

                if (_state == EntryState.Disposed || _state == EntryState.Failed)
                    break;

                int bytesRead;
                try
                {
                    bytesRead = await _innerStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error(ex, "[SharedStreamEntry] Pump read failed. DavItemId={DavItemId}", _davItemId);
                    TransitionToFailed(ex);
                    return;
                }

                if (bytesRead == 0)
                {
                    _completed = true;
                    Log.Debug("[SharedStreamEntry] Pump reached end of stream. DavItemId={DavItemId}", _davItemId);
                    // Signal all waiting readers that we're done
                    Interlocked.Exchange(ref _dataAvailableTcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                    return;
                }

                // Backpressure: wait until writing won't overwrite data the slowest reader needs
                while (true)
                {
                    if (_state == EntryState.Disposed || _state == EntryState.Failed)
                        return;

                    var minPos = SlowestReaderPosition;
                    // No readers tracked → write freely (grace period handles pump pausing)
                    if (minPos == null || _writePosition + bytesRead <= minPos.Value + _ringBufferSize)
                        break;

                    // Pump would overwrite data the slowest reader hasn't consumed — wait
                    try
                    {
                        await Volatile.Read(ref _readerAdvancedTcs).Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Re-check state and try again
                    }
                }

                // Write to ring buffer (may wrap around)
                var writePos = _writePosition;
                var ringOffset = (int)(writePos % _ringBufferSize);
                var firstChunk = Math.Min(bytesRead, _ringBufferSize - ringOffset);
                Buffer.BlockCopy(buffer, 0, _ringBuffer, ringOffset, firstChunk);
                if (firstChunk < bytesRead)
                {
                    Buffer.BlockCopy(buffer, firstChunk, _ringBuffer, 0, bytesRead - firstChunk);
                }

                Volatile.Write(ref _writePosition, writePos + bytesRead);

                // Signal ALL waiting readers (broadcast)
                Interlocked.Exchange(ref _dataAvailableTcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SharedStreamEntry] Pump loop unexpected error. DavItemId={DavItemId}", _davItemId);
            TransitionToFailed(ex);
        }
    }

    private void TransitionToFailed(Exception ex)
    {
        lock (_stateLock)
        {
            if (_state == EntryState.Disposed) return;

            _failure = ex;
            _state = EntryState.Failed;
            _graceTimer?.Dispose();
            _graceTimer = null;

            // Wake all waiting readers so they see the failure
            Interlocked.Exchange(ref _dataAvailableTcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            Log.Warning("[SharedStreamEntry] Entry failed, evicting. DavItemId={DavItemId}, Error={Error}", _davItemId, ex.Message);
        }

        // Evict from manager (outside lock to avoid deadlocks)
        _evictCallback(_davItemId);
        CleanupResources();
    }

    /// <summary>
    /// Set state to Disposed and clean up timer. Must be called inside _stateLock.
    /// Returns true if cleanup should proceed (caller must call evict + CleanupResources outside the lock).
    /// </summary>
    private bool SetDisposedState()
    {
        if (_state == EntryState.Disposed) return false;
        _state = EntryState.Disposed;
        _graceTimer?.Dispose();
        _graceTimer = null;
        return true;
    }

    private void CleanupResources()
    {
        try { _entryCts.Cancel(); } catch { /* best effort */ }

        try
        {
            _innerStream.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SharedStreamEntry] Error disposing inner stream");
        }

        _slot.Release();
        _pumpGate.Dispose();
        try { _contextScope?.Dispose(); } catch { /* best effort */ }
        try { _entryCts.Dispose(); } catch { /* best effort */ }
        Log.Debug("[SharedStreamEntry] Resources cleaned up, semaphore slot released. DavItemId={DavItemId}", _davItemId);
    }

    public void Dispose()
    {
        bool shouldCleanup;
        lock (_stateLock)
        {
            shouldCleanup = SetDisposedState();
        }

        if (shouldCleanup)
        {
            _evictCallback(_davItemId);
            CleanupResources();
        }
    }

    /// <summary>
    /// Clean up resources without evicting from SharedStreamManager.
    /// Used when this entry lost a TryAdd race and was never registered in the dictionary.
    /// </summary>
    internal void DisposeWithoutEvict()
    {
        lock (_stateLock)
        {
            if (_state == EntryState.Disposed) return;
            _state = EntryState.Disposed;
        }

        CleanupResources();
    }
}

