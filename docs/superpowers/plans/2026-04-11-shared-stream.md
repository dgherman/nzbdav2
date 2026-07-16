# Per-File Shared Stream Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Multiple concurrent HTTP requests for the same file share a single `BufferedSegmentStream` through a ring buffer, eliminating connection pool saturation from Stremio's parallel probes.

**Architecture:** Three new classes — `SharedStreamManager` (static lookup), `SharedStreamEntry` (owns BufferedSegmentStream + ring buffer + pump), `SharedStreamHandle` (per-reader cursor). `NzbFileStream.GetCombinedStream()` checks the manager before creating BufferedSegmentStreams directly. Entries survive across HTTP requests via a grace period timer. The `DavItemId` (Guid) from `ConnectionUsageContext` is the cache key.

**Tech Stack:** C# / .NET 10, no new dependencies

**Note:** The spec says `ConcurrentDictionary<int, SharedStreamEntry>` keyed by DavItemId, but `DavItemId` is `Guid?` in `ConnectionUsageDetails`. The dictionary key type must be `Guid`.

---

### Task 1: Add ConfigManager methods for shared stream settings

**Files:**
- Modify: `backend/Config/ConfigManager.cs`

- [ ] **Step 1: Add GetSharedStreamGracePeriod method**

In `backend/Config/ConfigManager.cs`, add after the `GetMaxConcurrentBufferedStreams()` method (around line 189):

```csharp
public int GetSharedStreamGracePeriod()
{
    return int.Parse(
        StringUtil.EmptyToNull(GetConfigValue("usenet.shared-stream-grace-period"))
        ?? "10"
    );
}
```

- [ ] **Step 2: Add GetSharedStreamBufferSize method**

Immediately after the method from Step 1:

```csharp
public int GetSharedStreamBufferSize()
{
    var mb = int.Parse(
        StringUtil.EmptyToNull(GetConfigValue("usenet.shared-stream-buffer-size"))
        ?? "15"
    );
    return Math.Max(2, mb) * 1024 * 1024; // Convert MB to bytes, minimum 2MB
}
```

- [ ] **Step 3: Build and verify**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add backend/Config/ConfigManager.cs
git commit -m "feat: add shared stream config methods (grace period + buffer size)"
```

---

### Task 2: Create SharedStreamHandle

**Files:**
- Create: `backend/Streams/SharedStreamHandle.cs`

This is the per-reader `Stream` that reads from the shared ring buffer. It depends on `SharedStreamEntry` at the type level, but we can define the interface it needs from the entry as internal fields. We build this first because it's the simplest component and defines the reader contract.

- [ ] **Step 1: Create SharedStreamHandle.cs**

```csharp
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Per-reader cursor into a SharedStreamEntry's ring buffer.
/// One handle per HTTP request. Implements Stream as a drop-in replacement
/// for BufferedSegmentStream in CombinedStream wrappers.
/// </summary>
public class SharedStreamHandle : Stream
{
    private readonly SharedStreamEntry _entry;
    private long _position;
    private bool _detached;
    private bool _disposed;

    internal SharedStreamHandle(SharedStreamEntry entry, long startPosition)
    {
        _entry = entry;
        _position = startPosition;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false; // NzbFileStream handles seeks by disposing/recreating
    public override bool CanWrite => false;
    public override long Length => _entry.StreamLength;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// True when this reader has fallen behind the ring buffer window
    /// and should be replaced with an unbuffered fallback.
    /// </summary>
    public bool IsDetached => _detached;

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed || _detached) return 0;

        var failure = _entry.Failure;
        if (failure != null)
            throw new IOException("Shared stream failed", failure);

        // Wait for data if we've caught up to the write position
        while (_position >= _entry.WritePosition)
        {
            if (_entry.IsCompleted)
                return 0; // Stream finished naturally

            failure = _entry.Failure;
            if (failure != null)
                throw new IOException("Shared stream failed", failure);

            try
            {
                await _entry.WaitForDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        // Check if we've fallen behind the ring buffer window
        var validStart = _entry.ValidRangeStart;
        if (_position < validStart)
        {
            Log.Warning("[SharedStreamHandle] Reader detached — position {Position} behind valid range start {ValidStart}", _position, validStart);
            _detached = true;
            return 0;
        }

        // Read from ring buffer
        var bytesAvailable = _entry.WritePosition - _position;
        var bytesToCopy = (int)Math.Min(Math.Min(bytesAvailable, count), int.MaxValue);
        _entry.CopyFromRingBuffer(_position, buffer, offset, bytesToCopy);
        _position += bytesToCopy;
        return bytesToCopy;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _entry.DetachReader();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _entry.DetachReader();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: Build** (will fail — `SharedStreamEntry` doesn't exist yet, that's expected)

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: Errors referencing `SharedStreamEntry` — confirms the handle compiles syntactically and that the entry interface is correct.

- [ ] **Step 3: Commit**

```bash
git add backend/Streams/SharedStreamHandle.cs
git commit -m "feat: add SharedStreamHandle — per-reader cursor for shared streams"
```

---

### Task 3: Create SharedStreamEntry

**Files:**
- Create: `backend/Streams/SharedStreamEntry.cs`

The entry owns one `BufferedSegmentStream`, a ring buffer, a pump task, and the lifetime state machine. This is the most complex component.

- [ ] **Step 1: Create SharedStreamEntry.cs with state and ring buffer**

```csharp
using System.Collections.Concurrent;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Owns a single BufferedSegmentStream and exposes its data through a ring buffer
/// to multiple SharedStreamHandle readers. Manages lifetime via reference counting
/// and a grace period timer.
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

    private long _writePosition; // Absolute byte offset of next write
    private int _readerCount;
    private volatile EntryState _state = EntryState.Active;
    private Exception? _failure;
    private bool _completed; // Inner stream returned 0 (natural end)
    private Timer? _graceTimer;
    private Task? _pumpTask;

    // Signaling: pump notifies readers when new data is available
    private readonly SemaphoreSlim _dataAvailable = new(0, int.MaxValue);
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
        Action<Guid> evictCallback)
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
        await _dataAvailable.WaitAsync(ct).ConfigureAwait(false);
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

            // Cancel grace timer if we're in grace period
            if (_state == EntryState.GracePeriod)
            {
                _graceTimer?.Dispose();
                _graceTimer = null;
                _state = EntryState.Active;
                _pumpGate.Set(); // Resume pump
                Log.Debug("[SharedStreamEntry] Reader attached during grace period, resuming pump. DavItemId={DavItemId}", _davItemId);
            }

            return new SharedStreamHandle(this, startPosition);
        }
    }

    /// <summary>
    /// Called by SharedStreamHandle.Dispose when a reader disconnects.
    /// </summary>
    internal void DetachReader()
    {
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
        lock (_stateLock)
        {
            if (_state != EntryState.GracePeriod) return;
            Log.Debug("[SharedStreamEntry] Grace period expired, disposing. DavItemId={DavItemId}", _davItemId);
            TransitionToDisposed();
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
                    _dataAvailable.Release(int.MaxValue / 2);
                    return;
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

                // Signal waiting readers
                try { _dataAvailable.Release(); } catch (SemaphoreFullException) { }
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
            try { _dataAvailable.Release(int.MaxValue / 2); } catch (SemaphoreFullException) { }

            Log.Warning("[SharedStreamEntry] Entry failed, evicting. DavItemId={DavItemId}, Error={Error}", _davItemId, ex.Message);
        }

        // Evict from manager (outside lock to avoid deadlocks)
        _evictCallback(_davItemId);
        CleanupResources();
    }

    private void TransitionToDisposed()
    {
        _state = EntryState.Disposed;
        _graceTimer?.Dispose();
        _graceTimer = null;

        // Evict from manager (outside lock to avoid deadlocks)
        _evictCallback(_davItemId);
        CleanupResources();
    }

    private void CleanupResources()
    {
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
        Log.Debug("[SharedStreamEntry] Resources cleaned up, semaphore slot released. DavItemId={DavItemId}", _davItemId);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_state == EntryState.Disposed) return;
            TransitionToDisposed();
        }
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors (SharedStreamHandle now has its dependency)

- [ ] **Step 3: Commit**

```bash
git add backend/Streams/SharedStreamEntry.cs
git commit -m "feat: add SharedStreamEntry — ring buffer, pump, and lifetime state machine"
```

---

### Task 4: Create SharedStreamManager

**Files:**
- Create: `backend/Streams/SharedStreamManager.cs`

Static manager class. Thin orchestration — just a concurrent dictionary with lookup/create/evict.

- [ ] **Step 1: Create SharedStreamManager.cs**

```csharp
using System.Collections.Concurrent;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Static manager for shared stream entries. Keyed by DavItemId (Guid).
/// Multiple HTTP requests for the same file share a single BufferedSegmentStream
/// through this manager.
/// </summary>
public static class SharedStreamManager
{
    private static readonly ConcurrentDictionary<Guid, SharedStreamEntry> s_entries = new();

    /// <summary>
    /// Try to attach to an existing shared stream for this file.
    /// Returns a handle if the entry exists and the position is within range, null otherwise.
    /// </summary>
    public static SharedStreamHandle? TryAttach(Guid davItemId, long startPosition)
    {
        if (!s_entries.TryGetValue(davItemId, out var entry))
            return null;

        var handle = entry.TryAttachReader(startPosition);
        if (handle != null)
        {
            Log.Debug("[SharedStreamManager] Attached to existing shared stream. DavItemId={DavItemId}, Position={Position}", davItemId, startPosition);
        }
        return handle;
    }

    /// <summary>
    /// Get or create a shared stream entry for this file.
    /// The factory is called only if no entry exists and a semaphore slot is acquired.
    /// Returns a handle, or null if no slot is available.
    /// </summary>
    /// <param name="davItemId">The file identifier</param>
    /// <param name="startPosition">The byte offset this reader starts at</param>
    /// <param name="streamLength">Total length of the stream</param>
    /// <param name="ringBufferSize">Ring buffer size in bytes</param>
    /// <param name="gracePeriodSeconds">Grace period before disposing after last reader detaches</param>
    /// <param name="factory">Creates a BufferedSegmentStream. Only called if creating a new entry.
    /// Must NOT call SetAcquiredSlot — the entry manages the semaphore slot.</param>
    public static SharedStreamHandle? GetOrCreate(
        Guid davItemId,
        long startPosition,
        long streamLength,
        int ringBufferSize,
        int gracePeriodSeconds,
        Func<BufferedSegmentStream> factory)
    {
        // Fast path: entry already exists
        if (s_entries.TryGetValue(davItemId, out var existing))
        {
            var handle = existing.TryAttachReader(startPosition);
            if (handle != null)
            {
                Log.Debug("[SharedStreamManager] Attached to existing entry (race). DavItemId={DavItemId}", davItemId);
                return handle;
            }
            // Entry exists but can't attach (position out of range, or entry is dying)
            // Fall through to try creating a new one — the old one will evict itself
        }

        // Acquire a semaphore slot
        var slot = BufferedSegmentStream.TryAcquireSlot();
        if (slot == null)
        {
            Log.Debug("[SharedStreamManager] No semaphore slot available. DavItemId={DavItemId}", davItemId);
            return null;
        }

        try
        {
            var innerStream = factory();

            var entry = new SharedStreamEntry(
                innerStream,
                slot,
                davItemId,
                startPosition,
                streamLength,
                ringBufferSize,
                gracePeriodSeconds,
                Evict
            );

            // Try to add — if another thread raced us, dispose our entry and attach to theirs
            if (!s_entries.TryAdd(davItemId, entry))
            {
                entry.Dispose();
                // Try the winner's entry
                if (s_entries.TryGetValue(davItemId, out var winner))
                {
                    return winner.TryAttachReader(startPosition);
                }
                return null;
            }

            entry.StartPump();
            Log.Information("[SharedStreamManager] Created shared stream entry. DavItemId={DavItemId}, Position={Position}, BufferSize={BufferSize}MB",
                davItemId, startPosition, ringBufferSize / (1024 * 1024));

            // The entry was created with readerCount=1, return the first handle
            return new SharedStreamHandle(entry, startPosition);
        }
        catch
        {
            slot.Release();
            throw;
        }
    }

    /// <summary>
    /// Remove an entry from the manager. Called by SharedStreamEntry on grace period expiry or failure.
    /// </summary>
    public static void Evict(Guid davItemId)
    {
        if (s_entries.TryRemove(davItemId, out _))
        {
            Log.Debug("[SharedStreamManager] Evicted entry. DavItemId={DavItemId}", davItemId);
        }
    }

    /// <summary>
    /// Get the number of active entries (for diagnostics/debugging).
    /// </summary>
    public static int ActiveEntryCount => s_entries.Count;
}
```

- [ ] **Step 2: Build and verify**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add backend/Streams/SharedStreamManager.cs
git commit -m "feat: add SharedStreamManager — static cache for per-file shared streams"
```

---

### Task 5: Integrate SharedStreamManager into NzbFileStream

**Files:**
- Modify: `backend/Streams/NzbFileStream.cs`

Two changes: (1) `GetCombinedStream()` checks `SharedStreamManager` before creating a `BufferedSegmentStream` directly, and (2) `ReadAsync()` handles premature EOF (reader detached from ring buffer).

- [ ] **Step 1: Add shared stream check in GetCombinedStream**

In `backend/Streams/NzbFileStream.cs`, replace the buffered streaming block (lines 262-352, starting from `// Disable buffered streaming for Queue processing` through the `catch { acquiredSlot.Release(); throw; }` closing brace) with:

```csharp
        // Disable buffered streaming for Queue processing since it only reads small amounts
        // (e.g., just the first segment for file size detection)
        var shouldUseBufferedStreaming = _useBufferedStreaming &&
            _usageContext.UsageType != ConnectionUsageType.Queue &&
            _usageContext.UsageType != ConnectionUsageType.QueueAnalysis;

        // Calculate the byte offset where this stream starts
        var segmentByteOffset = _segmentOffsets != null
            ? _segmentOffsets[firstSegmentIndex]
            : firstSegmentIndex * (_fileSize / _fileSegmentIds.Length);
        var totalBaseOffset = (_usageContext.DetailsObject?.BaseByteOffset ?? 0) + segmentByteOffset;

        // Try shared stream path first (for streaming requests with a DavItemId)
        var davItemId = _usageContext.DetailsObject?.DavItemId;
        if (shouldUseBufferedStreaming && _concurrentConnections >= 3 && _fileSegmentIds.Length > _concurrentConnections
            && davItemId.HasValue)
        {
            // Try to attach to an existing shared stream
            var existingHandle = SharedStreamManager.TryAttach(davItemId.Value, totalBaseOffset);
            if (existingHandle != null)
            {
                Serilog.Log.Debug("[NzbFileStream] Attached to existing shared stream for DavItemId={DavItemId} at offset {Offset}",
                    davItemId.Value, totalBaseOffset);
                _contextScope = _streamCts.Token.SetScopedContext(_usageContext);
                _cancellationRegistration = ct.Register(() =>
                {
                    if (!_disposed) { try { _streamCts.Cancel(); } catch (ObjectDisposedException) { } }
                });
                return new CombinedStream(new[] { Task.FromResult<Stream>(existingHandle) });
            }

            // Try to create a new shared stream entry
            var sharedHandle = SharedStreamManager.GetOrCreate(
                davItemId.Value,
                totalBaseOffset,
                _fileSize,
                _sharedStreamBufferSize,
                _sharedStreamGracePeriod,
                () =>
                {
                    var detailsObj = new ConnectionUsageDetails
                    {
                        Text = _usageContext.Details ?? "",
                        JobName = _usageContext.DetailsObject?.JobName,
                        AffinityKey = _usageContext.DetailsObject?.AffinityKey,
                        DavItemId = _usageContext.DetailsObject?.DavItemId,
                        FileDate = _usageContext.DetailsObject?.FileDate,
                        FileSize = _usageContext.DetailsObject?.FileSize ?? _fileSize,
                        BaseByteOffset = totalBaseOffset
                    };
                    var bufferedContext = new ConnectionUsageContext(
                        ConnectionUsageType.BufferedStreaming, detailsObj);

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
                                remainingSegmentSizes[i] = _segmentOffsets[originalIndex + 1] - _segmentOffsets[originalIndex];
                        }
                    }

                    _contextScope = _streamCts.Token.SetScopedContext(bufferedContext);
                    var bufferedContextCt = _streamCts.Token;
                    var bufferedStream = new BufferedSegmentStream(
                        remainingSegments, remainingSize, _client,
                        _concurrentConnections, _bufferSize, bufferedContextCt,
                        bufferedContext, remainingSegmentSizes, _segmentFallbacks, firstSegmentIndex);
                    // Do NOT call SetAcquiredSlot — SharedStreamEntry manages the slot
                    return bufferedStream;
                });

            if (sharedHandle != null)
            {
                Serilog.Log.Debug("[NzbFileStream] Created new shared stream for DavItemId={DavItemId} at offset {Offset}",
                    davItemId.Value, totalBaseOffset);
                _cancellationRegistration = ct.Register(() =>
                {
                    if (!_disposed) { try { _streamCts.Cancel(); } catch (ObjectDisposedException) { } }
                });
                return new CombinedStream(new[] { Task.FromResult<Stream>(sharedHandle) });
            }
            // Fall through to direct BufferedSegmentStream or unbuffered
        }

        // Direct BufferedSegmentStream path (no DavItemId, or shared stream not available)
        var acquiredSlot = shouldUseBufferedStreaming && _concurrentConnections >= 3 && _fileSegmentIds.Length > _concurrentConnections
            ? BufferedSegmentStream.TryAcquireSlot() : null;
        if (acquiredSlot != null)
        {
            try
            {
                var detailsObj = new ConnectionUsageDetails
                {
                    Text = _usageContext.Details ?? "",
                    JobName = _usageContext.DetailsObject?.JobName,
                    AffinityKey = _usageContext.DetailsObject?.AffinityKey,
                    DavItemId = _usageContext.DetailsObject?.DavItemId,
                    FileDate = _usageContext.DetailsObject?.FileDate,
                    FileSize = _usageContext.DetailsObject?.FileSize ?? _fileSize,
                    BaseByteOffset = totalBaseOffset
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
                    remainingSize,
                    _client,
                    _concurrentConnections,
                    _bufferSize,
                    bufferedContextCt,
                    bufferedContext,
                    remainingSegmentSizes,
                    _segmentFallbacks,
                    firstSegmentIndex
                );
                bufferedStream.SetAcquiredSlot(acquiredSlot);

                _cancellationRegistration = ct.Register(() =>
                {
                    if (!_disposed)
                    {
                        try { _streamCts.Cancel(); } catch (ObjectDisposedException) { }
                    }
                });

                return new CombinedStream(new[] { Task.FromResult<Stream>(bufferedStream) });
            }
            catch
            {
                acquiredSlot.Release();
                throw;
            }
        }
```

- [ ] **Step 2: Add shared stream config fields to NzbFileStream constructor**

Add two new fields and constructor parameters. In the field declarations (after `_segmentFallbacks`, around line 18):

```csharp
    private readonly int _sharedStreamBufferSize;
    private readonly int _sharedStreamGracePeriod;
```

Add two new parameters to the constructor signature (after `segmentFallbacks`):

```csharp
        int sharedStreamBufferSize = 15 * 1024 * 1024,
        int sharedStreamGracePeriod = 10
```

And assign them in the constructor body (after `_segmentFallbacks = segmentFallbacks;`):

```csharp
        _sharedStreamBufferSize = sharedStreamBufferSize;
        _sharedStreamGracePeriod = sharedStreamGracePeriod;
```

- [ ] **Step 3: Add premature EOF handling in ReadAsync**

In `ReadAsync` (around line 99-100), change:

```csharp
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _position += read;

        // Reset consecutive seek counter on successful read
        if (read > 0)
        {
            _consecutiveSeeksToSameOffset = 0;
        }
```

to:

```csharp
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _position += read;

        // Reset consecutive seek counter on successful read
        if (read > 0)
        {
            _consecutiveSeeksToSameOffset = 0;
        }

        // Handle premature EOF: SharedStreamHandle returns 0 when reader detaches
        // (fell behind ring buffer window). Recreate inner stream — will fall back to unbuffered.
        if (read == 0 && _position < _fileSize)
        {
            Serilog.Log.Debug("[NzbFileStream] Premature EOF at position {Position}/{FileSize}, recreating inner stream", _position, _fileSize);
            _innerStream?.Dispose();
            _innerStream = null;
            // Re-attempt the read with a new inner stream
            _innerStream = await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
            read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            _position += read;
        }
```

- [ ] **Step 4: Update UsenetStreamingClient.GetFileStream to pass shared stream config**

In `backend/Clients/Usenet/UsenetStreamingClient.cs`, find the overload at line 288:

```csharp
    public NzbFileStream GetFileStream(string[] segmentIds, long fileSize, int concurrentConnections, ConnectionUsageContext? usageContext = null, bool useBufferedStreaming = true, int? bufferSize = null, long[]? segmentSizes = null, Dictionary<int, string[]>? segmentFallbacks = null)
    {
        // Use config value if not specified
        var actualBufferSize = bufferSize ?? _configManager.GetStreamBufferSize();
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections, usageContext, useBufferedStreaming, actualBufferSize, segmentSizes, segmentFallbacks);
    }
```

Change the `return` line to:

```csharp
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections, usageContext, useBufferedStreaming, actualBufferSize, segmentSizes, segmentFallbacks,
            _configManager.GetSharedStreamBufferSize(), _configManager.GetSharedStreamGracePeriod());
```

Also update the other overloads that create `NzbFileStream`. At line 278 (the `NzbFile` overload):

```csharp
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections, usageContext, bufferSize: bufferSize,
            sharedStreamBufferSize: _configManager.GetSharedStreamBufferSize(), sharedStreamGracePeriod: _configManager.GetSharedStreamGracePeriod());
```

At line 285 (the other `NzbFile` overload):

```csharp
        return new NzbFileStream(nzbFile.GetSegmentIds(), fileSize, _client, concurrentConnections, usageContext, bufferSize: bufferSize, segmentSizes: segmentSizes,
            sharedStreamBufferSize: _configManager.GetSharedStreamBufferSize(), sharedStreamGracePeriod: _configManager.GetSharedStreamGracePeriod());
```

- [ ] **Step 5: Build and verify**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors

- [ ] **Step 6: Commit**

```bash
git add backend/Streams/NzbFileStream.cs backend/Clients/Usenet/UsenetStreamingClient.cs
git commit -m "feat: integrate SharedStreamManager into NzbFileStream

NzbFileStream.GetCombinedStream() now checks SharedStreamManager for an
existing shared stream before creating a direct BufferedSegmentStream.
ReadAsync handles premature EOF from detached SharedStreamHandle readers
by recreating the inner stream (falls back to unbuffered)."
```

---

### Task 6: Build, smoke test, and final commit

**Files:**
- All files from Tasks 1-5

- [ ] **Step 1: Full build**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors, 0 warnings related to new code

- [ ] **Step 2: Verify no regressions in existing paths**

Grep to confirm the direct `BufferedSegmentStream` path still exists as fallback (for non-streaming usage types, benchmarks, etc.):

```bash
grep -n "BufferedSegmentStream.TryAcquireSlot" backend/Streams/NzbFileStream.cs
```

Expected: Should find the slot acquisition in the direct fallback path (after the shared stream section).

Also verify queue processing still skips both shared and buffered:

```bash
grep -n "ConnectionUsageType.Queue" backend/Streams/NzbFileStream.cs
```

Expected: Should find the `shouldUseBufferedStreaming` check that excludes Queue and QueueAnalysis.

- [ ] **Step 3: Verify SharedStreamManager key type**

```bash
grep -n "ConcurrentDictionary<Guid" backend/Streams/SharedStreamManager.cs
```

Expected: `ConcurrentDictionary<Guid, SharedStreamEntry>` — confirms we're using `Guid`, not `int` as the spec's pseudocode suggested.

- [ ] **Step 4: Commit (if any fixes were needed)**

```bash
git add -A
git commit -m "fix: address build issues from shared stream integration"
```
