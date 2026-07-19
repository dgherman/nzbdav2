using System.Collections.Concurrent;
using System.Diagnostics;
using NzbWebDAV.Metrics;
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

    /// <summary>
    /// Why <see cref="TryAttachReader(long, out AttachRejection, out long)"/> refused a reader.
    /// Reported so the manager can record how far out of range the reader was — the measurement
    /// that separates "a bigger ring would have caught this" from "this file needs a second entry".
    /// </summary>
    public enum AttachRejection
    {
        None,
        EntryUnusable,      // Disposed or Failed
        BeforeBase,         // Earlier than where this entry's pump started
        BehindWindow,       // Fell out of the trailing edge of the ring
        AheadOfFrontier,    // Seeked past the pump by more than a ring's worth
        PastEnd,            // Beyond the end of the stream
    }

    /// <summary>Why an entry was torn down. Label for the lifetime histogram.</summary>
    public enum TeardownCause { GraceExpired, Failed, Disposed, RaceLost }

    private readonly byte[] _ringBuffer;
    private readonly int _ringBufferSize;
    private readonly long _basePosition; // Absolute byte offset of first byte written
    // Typed as Stream rather than BufferedSegmentStream: the pump only needs Read/Dispose,
    // and the looser type lets the entry be driven by an in-memory stream in tests.
    private readonly Stream _innerStream;
    private readonly SemaphoreSlim _slot; // Acquired semaphore from TryAcquireSlot
    private readonly Guid _davItemId;
    private readonly int _gracePeriodSeconds;
    // Calls SharedStreamManager.Evict. Carries the entry itself, not just the id: a file can now
    // hold several entries covering different regions, so the id alone no longer identifies one.
    private readonly Action<Guid, SharedStreamEntry> _evictCallback;
    private readonly CancellationTokenSource _entryCts; // Entry-scoped cancellation, independent of any request
    // Captured once: reading _entryCts.Token after the CTS is disposed throws, and the pump can
    // reach its next wait after cleanup has already run. A captured token stays usable — once
    // cancelled, waits on it throw OperationCanceledException without touching the source.
    private readonly CancellationToken _entryToken;
    private readonly IDisposable? _contextScope; // Entry-scoped cancellation-token context lifetime

    private long _writePosition; // Absolute byte offset of next write
    private int _readerCount;
    private volatile EntryState _state = EntryState.Active;
    private Exception? _failure;
    private volatile bool _completed; // Inner stream returned 0 (natural end)
    private Timer? _graceTimer;
    private Task? _pumpTask;
    private int _cleanedUp; // 0 = not yet, 1 = done. Guards CleanupResources against running twice.

    // How long the pump will keep a backpressured inner stream alive while no reader consumes.
    // Long enough for a real pause, short enough that a reader which silently went away releases
    // its buffers rather than pinning them for the life of the process.
    private const int MaxPausedTouchSeconds = 600;
    private long _backpressureSinceTimestamp = Stopwatch.GetTimestamp();

    // Per-reader position tracking for backpressure
    private readonly ConcurrentDictionary<int, long> _readerPositions = new();
    private int _nextHandleId;

    // Lifetime accounting (issue #18). _readersServed counts every reader the entry ever had,
    // including the creating one, so an entry that never shared with anyone reports 1.
    private readonly long _createdTimestamp = Stopwatch.GetTimestamp();
    private int _readersServed = 1;
    private long _bytesPumped;
    // Set by the teardown path before CleanupResources so the lifetime is filed under the right
    // cause. CleanupResources itself runs from several paths and cannot tell them apart.
    private TeardownCause _teardownCause = TeardownCause.Disposed;

    // Signaling: pump notifies ALL waiting readers when new data is available (broadcast)
    private TaskCompletionSource _dataAvailableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Signaling: readers notify pump when they advance (for backpressure release)
    private TaskCompletionSource _readerAdvancedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Gate: pauses pump when no readers are active
    private readonly ManualResetEventSlim _pumpGate = new(true); // Start open (first reader is attaching)

    private readonly object _stateLock = new();

    public long WritePosition => Volatile.Read(ref _writePosition);
    public int ActiveReaders => Volatile.Read(ref _readerCount);
    public long ValidRangeStart => Math.Max(_basePosition, WritePosition - _ringBufferSize);
    public bool IsCompleted => _completed;
    public Exception? Failure => _failure;
    public long StreamLength { get; }
    public EntryState State => _state;

    /// <summary>The pump task, once started. Exposed so tests can assert it actually exits on teardown.</summary>
    internal Task? PumpTask => Volatile.Read(ref _pumpTask);

    public SharedStreamEntry(
        Stream innerStream,
        SemaphoreSlim slot,
        Guid davItemId,
        long basePosition,
        long streamLength,
        int ringBufferSize,
        int gracePeriodSeconds,
        Action<Guid, SharedStreamEntry> evictCallback,
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
        _entryToken = entryCts.Token;
        _contextScope = contextScope;
        _readerCount = 1; // First reader is being created by the caller
    }

    /// <summary>
    /// Start the background pump task. Must be called after construction.
    /// </summary>
    public void StartPump()
    {
        // Volatile: the entry is published to SharedStreamManager before this runs, so cleanup on
        // another thread must not read a stale null and skip waiting for the pump.
        Volatile.Write(ref _pumpTask, Task.Run(PumpLoop));
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
        // A reader is demonstrably alive, so the paused-touch budget starts over. Only a reader that
        // never advances again can exhaust it.
        Volatile.Write(ref _backpressureSinceTimestamp, Stopwatch.GetTimestamp());
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
    /// Captures the current data-available signal.
    /// Callers MUST capture this BEFORE re-checking WritePosition/IsCompleted/Failure, and then
    /// await the captured task. The pump swaps in a fresh TCS every time it publishes data, so a
    /// caller that checks its exit conditions first and only then reads the signal can await a TCS
    /// the pump has already moved past — the wakeup is lost and the reader hangs until its request
    /// token fires. Capturing first closes that window: any signal raised after the capture either
    /// completes the captured task or is followed by a state change the re-check observes.
    /// </summary>
    public Task CaptureDataSignal() => Volatile.Read(ref _dataAvailableTcs).Task;

    /// <summary>
    /// Attach a new reader. Returns a SharedStreamHandle if the position is within range, null otherwise.
    /// </summary>
    public SharedStreamHandle? TryAttachReader(long startPosition)
        => TryAttachReader(startPosition, out _, out _);

    /// <summary>
    /// Attach a new reader, reporting why on refusal.
    /// </summary>
    /// <param name="rejection">Why the attach was refused, or <see cref="AttachRejection.None"/> on success.</param>
    /// <param name="distanceFromFrontier">Signed distance from the write frontier at the moment of
    /// refusal: positive means the reader seeked past the pump, negative means it fell behind.
    /// Meaningless when the attach succeeded.</param>
    public SharedStreamHandle? TryAttachReader(long startPosition, out AttachRejection rejection, out long distanceFromFrontier)
    {
        lock (_stateLock)
        {
            distanceFromFrontier = startPosition - WritePosition;

            if (_state == EntryState.Disposed || _state == EntryState.Failed)
            {
                rejection = AttachRejection.EntryUnusable;
                return null;
            }

            // Position must be >= basePosition and within the ring buffer window
            if (startPosition < _basePosition)
            {
                rejection = AttachRejection.BeforeBase;
                return null;
            }

            var validStart = ValidRangeStart;
            if (startPosition < validStart)
            {
                rejection = AttachRejection.BehindWindow;
                return null;
            }

            // Position can be at or slightly ahead of the write frontier (the reader waits
            // briefly for the in-order pump to reach it). But reject positions far ahead of
            // the frontier: the pump fills the ring sequentially, so a reader that joins far
            // ahead — e.g. a player seeking to the Matroska tail/Cues (~hundreds of MB out)
            // to read the index before playback — would block until the pump crawled all the
            // way there, stalling the read until it times out and the player re-opens (an
            // endless "buffering" loop). Returning null makes the caller spin up its own
            // stream at the seek target, which serves the tail immediately.
            if (startPosition > WritePosition + _ringBufferSize)
            {
                rejection = AttachRejection.AheadOfFrontier;
                return null;
            }

            // Don't allow beyond stream length.
            if (startPosition > _basePosition + StreamLength)
            {
                rejection = AttachRejection.PastEnd;
                return null;
            }

            rejection = AttachRejection.None;
            Interlocked.Increment(ref _readerCount);
            Interlocked.Increment(ref _readersServed);
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
            _teardownCause = TeardownCause.GraceExpired;
            shouldCleanup = SetDisposedState();
        }

        if (shouldCleanup)
        {
            _evictCallback(_davItemId, this);
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
                // Block if no readers are active (grace period).
                // The token is essential: cleanup opens the gate AND cancels, but a pump parked on
                // an untokened Wait() would stay parked forever if the gate were disposed from under
                // it (ManualResetEventSlim.Dispose does not release waiters), leaking this thread on
                // every normal teardown.
                _pumpGate.Wait(_entryToken);

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
                    SignalTerminalState();
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

                    // Readers are attached but not consuming — a paused player. Keep the inner stream's
                    // idle watchdog at bay: it only sees reads, and this pump has deliberately stopped
                    // reading. Without this the workers self-cancel after 60s and resume pays a full
                    // cold rebuild.
                    //
                    // Bounded, because "a reader is attached" is weaker evidence than it looks: the
                    // watchdog exists for clients that vanished without the server noticing (a
                    // disconnect that never propagated back through a reverse proxy), and such a reader
                    // stays attached forever. Holding the stream open for it would pin its buffers for
                    // good. A real paused player resumes well inside this window; a ghost gets reaped.
                    if (Stopwatch.GetElapsedTime(Volatile.Read(ref _backpressureSinceTimestamp)).TotalSeconds < MaxPausedTouchSeconds)
                        (_innerStream as ITouchableStream)?.Touch();

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
                Interlocked.Add(ref _bytesPumped, bytesRead);

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
        finally
        {
            // Every started pump must reach this line. A missing exit log for an evicted entry means
            // a pump thread leaked — the failure mode this loop's cancellable gate wait exists to prevent.
            Log.Debug("[SharedStreamEntry] Pump exited. DavItemId={DavItemId}, State={State}", _davItemId, _state);
        }
    }

    /// <summary>
    /// Wakes every waiting reader for a state they can never be woken out of again (EOF or failure).
    /// Unlike the per-chunk pulse this does NOT swap in a fresh TCS: the pump is gone, so a fresh one
    /// would never be completed and a reader that captured it would wait forever. Leaving the signal
    /// permanently completed is correct — later readers wake at once and re-check the terminal state.
    /// </summary>
    private void SignalTerminalState()
    {
        Volatile.Read(ref _dataAvailableTcs).TrySetResult();
    }

    private void TransitionToFailed(Exception ex)
    {
        lock (_stateLock)
        {
            if (_state == EntryState.Disposed) return;

            _failure = ex;
            _state = EntryState.Failed;
            _teardownCause = TeardownCause.Failed;
            _graceTimer?.Dispose();
            _graceTimer = null;

            // Wake all waiting readers so they see the failure
            SignalTerminalState();

            Log.Warning("[SharedStreamEntry] Entry failed, evicting. DavItemId={DavItemId}, Error={Error}", _davItemId, ex.Message);
        }

        // Evict from manager (outside lock to avoid deadlocks)
        _evictCallback(_davItemId, this);
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
        // Exactly once. TransitionToFailed cleans up without marking the entry Disposed, so a later
        // Dispose() would otherwise clean up a second time and release the concurrent-stream slot twice
        // (SemaphoreFullException), on top of touching an already-disposed gate.
        if (Interlocked.Exchange(ref _cleanedUp, 1) == 1) return;

        RecordLifetime();

        // Wake the pump before tearing anything down. It may be parked on the gate (grace period)
        // or blocked in the inner stream's read: Set() covers the former, Cancel() the latter.
        // Both are needed — the gate wait is what silently leaked pump threads before.
        _pumpGate.Set();
        try { _entryCts.Cancel(); } catch { /* best effort */ }

        try
        {
            _innerStream.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SharedStreamEntry] Error disposing inner stream");
        }

        // Release the scarce concurrent-stream slot immediately — never behind pump exit.
        _slot.Release();
        try { _contextScope?.Dispose(); } catch { /* best effort */ }

        DisposePumpScopedResources();

        Log.Debug("[SharedStreamEntry] Resources cleaned up, semaphore slot released. DavItemId={DavItemId}", _davItemId);
    }

    /// <summary>
    /// Files this entry's lifetime, readers served and bytes pumped. Called once, from
    /// CleanupResources, which is already guarded to run exactly once.
    /// </summary>
    private void RecordLifetime()
    {
        var lifetime = Stopwatch.GetElapsedTime(_createdTimestamp).TotalSeconds;
        var readers = Volatile.Read(ref _readersServed);
        var bytes = Interlocked.Read(ref _bytesPumped);
        var cause = _teardownCause switch
        {
            TeardownCause.GraceExpired => "grace_expired",
            TeardownCause.Failed => "failed",
            TeardownCause.RaceLost => "race_lost",
            _ => "disposed",
        };

        AppMetrics.SharedStreamEntryLifetimeSeconds.WithLabels(cause).Observe(lifetime);
        AppMetrics.SharedStreamEntryReadersServed.Observe(readers);
        AppMetrics.SharedStreamEntryBytesPumped.Observe(bytes);

        // Information, not Debug: this is the per-entry record issue #18 needs read back out of a
        // production container, and Debug is off there.
        Log.Information("[SharedStreamEntry] Entry torn down. DavItemId={DavItemId}, Cause={Cause}, " +
                        "Lifetime={Lifetime:F1}s, ReadersServed={Readers}, Pumped={Pumped}MB, Base={Base}, Frontier={Frontier}",
            _davItemId, cause, lifetime, readers, bytes / (1024 * 1024), _basePosition, WritePosition);
    }

    /// <summary>
    /// Disposes the two things the pump can be waiting on — the gate and the entry CTS — but only
    /// once the pump has actually exited, and never inline: TransitionToFailed calls CleanupResources
    /// from inside the pump itself, so an inline wait would self-deadlock. If the pump does not exit
    /// promptly we log and leave both to the GC rather than fault a live pump; that is strictly
    /// better than the disposal-under-waiter bug this replaces.
    /// </summary>
    private void DisposePumpScopedResources()
    {
        var pump = Volatile.Read(ref _pumpTask);
        if (pump == null)
        {
            // StartPump was never called (e.g. the TryAdd race loser) — nothing can be waiting.
            _pumpGate.Dispose();
            try { _entryCts.Dispose(); } catch { /* best effort */ }
            return;
        }

        _ = Task.Run(async () =>
        {
            var finished = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (finished != pump)
            {
                Log.Warning("[SharedStreamEntry] Pump still running 5s after cleanup; leaving gate/CTS to the GC. DavItemId={DavItemId}", _davItemId);
                return;
            }

            _pumpGate.Dispose();
            try { _entryCts.Dispose(); } catch { /* best effort */ }
        });
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
            _evictCallback(_davItemId, this);
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
            _teardownCause = TeardownCause.RaceLost;
        }

        CleanupResources();
    }
}

