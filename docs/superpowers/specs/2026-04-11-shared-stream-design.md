# Per-File Shared Stream Design

## Problem

Media players like Stremio send multiple concurrent HTTP requests for the same file within 1-2 seconds:

1. **Probe 1:** GET from offset 0 — reads EBML header (~few KB), closes
2. **Probe 2:** Range request near end — reads MKV Cues/seek index (~few KB), closes
3. **Probe 3:** Range request to metadata position — reads a bit, closes
4. **Probe 4:** GET from actual play position — this is the real sustained stream

Each request currently creates an independent `NzbFileStream` → `BufferedSegmentStream`, each demanding ~20 Usenet connections. With 4 concurrent BufferedSegmentStreams competing for the same connection pool (~17 slots), connections churn (acquired and released in 0.1-0.5s) and all streams truncate at ~25MB.

The existing concurrent stream cap (`TryAcquireSlot`, default 2) reduces but doesn't solve this: 2 BufferedSegmentStreams still compete for connections, and ephemeral probe requests waste those slots on data they'll never fully consume.

## Solution

A shared stream layer between `NzbFileStream` and `BufferedSegmentStream`. Multiple HTTP requests for the same file share a single `BufferedSegmentStream` through a ring buffer. Probes and the real stream all read from the same download — one set of connections, one download cursor.

## Architecture

Three new components:

```
SharedStreamManager (static singleton)
└── ConcurrentDictionary<int, SharedStreamEntry> (keyed by DavItemId)

SharedStreamEntry (one per file being streamed)
├── BufferedSegmentStream (single instance, does all downloading)
├── Ring buffer (fixed-size circular byte buffer)
├── Pump task (reads from BufferedSegmentStream → ring buffer)
├── Reader count + grace timer
└── State machine (Active / GracePeriod / Failed / Disposed)

SharedStreamHandle : Stream (one per HTTP request)
├── Read cursor (absolute byte offset into ring buffer)
└── Detach flag (set when reader falls behind window)
```

**Data flow:**

1. `NzbFileStream.GetCombinedStream()` asks `SharedStreamManager` for an entry matching this file's DavItemId
2. If no entry exists and a semaphore slot is available → create new `SharedStreamEntry` with fresh `BufferedSegmentStream`
3. If entry exists and the reader's start position is within the ring buffer window → attach as new `SharedStreamHandle`
4. If entry exists but position is outside the window → skip sharing, use unbuffered sequential
5. The entry's pump task is the sole consumer of `BufferedSegmentStream.ReadAsync()`, writing downloaded bytes into the ring buffer
6. Each `SharedStreamHandle` reads from the ring buffer at its own position independently

**Key invariant:** The pump pauses when no readers are active (grace period). This freezes ring buffer contents so a new reader arriving at position 0 still finds early bytes. The BufferedSegmentStream's internal channel fills up, and its fetch workers naturally pause too. When a new reader attaches, the pump resumes.

## SharedStreamManager

Static class with a `ConcurrentDictionary<int, SharedStreamEntry>` keyed by DavItemId.

**Methods:**

- `TryAttach(davItemId, startPosition, ...) → SharedStreamHandle?` — fast path. If an entry exists and startPosition is within the ring buffer window, creates and returns a new handle.
- `GetOrCreate(davItemId, factory, ...) → SharedStreamHandle?` — if no entry exists, acquires a semaphore slot via `TryAcquireSlot()`, creates a new entry with a fresh BufferedSegmentStream via the factory, inserts into dictionary, returns first handle. Returns null if no slot available.
- `Evict(davItemId)` — removes entry from dictionary. Called by grace period expiry or stream failure.

**Scope:** Only applies when `DavItemId` is available in the usage context and the usage type is streaming. Queue processing, benchmarks, and analysis skip the manager entirely and use existing logic.

## SharedStreamEntry

Owns one `BufferedSegmentStream` and manages a shared ring buffer with reference-counted readers.

**State:**

```
Fields:
├── BufferedSegmentStream _innerStream
├── byte[] _ringBuffer              (fixed-size circular buffer)
├── long _writePosition             (absolute byte offset of next write)
├── long _basePosition              (absolute byte offset of the first byte ever written)
├── int _readerCount                (managed with Interlocked)
├── Task _pumpTask
├── Timer _graceTimer
├── SemaphoreSlim _slot             (acquired semaphore from TryAcquireSlot)
├── EntryState _state               (Active / GracePeriod / Failed / Disposed)
├── ManualResetEventSlim _dataAvailable  (signals readers when pump writes)
├── ManualResetEventSlim _pumpGate       (pauses pump when no readers)
└── Exception? _failure
```

**Ring buffer mechanics:**

Fixed-size byte array. The pump writes circularly. Position tracking uses absolute byte offsets, mapped to the ring buffer via modulo.

- **Valid range:** `[max(_basePosition, _writePosition - ringBufferSize), _writePosition)` — bytes a reader can access. The lower bound is clamped to `_basePosition` (the stream's start offset) because no data exists before that.
- A reader at absolute position P can read if P falls within the valid range
- If P < valid range start → reader fell behind, must detach
- If P < `_basePosition` → position predates this stream, cannot attach

**Pump task:**

```
loop:
    wait on _pumpGate (blocks when no readers active)
    read chunk from BufferedSegmentStream.ReadAsync (256KB)
    if read == 0 → stream complete, transition to Disposed, break
    if error → set _failure, transition to Failed, signal readers, evict, break
    copy chunk into ring buffer at (_writePosition % ringBufferSize)
    advance _writePosition
    signal _dataAvailable
```

**Lifetime state machine:**

```
Active ──(last reader detaches)──► GracePeriod ──(timer expires)──► Disposed
  │                                     │                              ▲
  │                               (new reader)                         │
  │                                     │                              │
  │                                  Active                            │
  │                                                                    │
  └──────(pump error / unrecoverable failure)──► Failed ───────────────┘
```

- **Active:** ≥1 reader attached. Pump runs. Grace timer cancelled.
- **GracePeriod:** 0 readers. Pump paused via `_pumpGate`. Grace timer ticking (default 10s). New reader → back to Active, pump resumes.
- **Failed:** Pump hit an unrecoverable error. Entry evicts from manager immediately. All current readers get the exception on next read. No grace period for failed entries.
- **Disposed:** Grace timer expired or stream completed naturally. BufferedSegmentStream disposed, semaphore slot released, entry removed from manager.

## SharedStreamHandle

Implements `Stream`. One per HTTP request that attaches to a `SharedStreamEntry`. Drop-in replacement wherever `BufferedSegmentStream` was used as the inner stream.

**State:**

```
├── SharedStreamEntry _entry
├── long _position      (absolute byte offset this reader is at)
├── bool _detached
└── CancellationToken _ct
```

**ReadAsync behavior:**

```
if _detached → return 0
if _entry._state == Failed → throw _entry._failure
if _position >= _entry._writePosition → wait on _dataAvailable
if _position < max(_entry._basePosition, _entry._writePosition - ringBufferSize) → set _detached, return 0

bytesAvailable = _entry._writePosition - _position
bytesToCopy = min(bytesAvailable, count)
copy from ring buffer at (_position % ringBufferSize) into caller's buffer
advance _position
return bytesToCopy
```

**Seeking:** Not supported by the handle. `NzbFileStream` handles seeks by disposing the inner stream and creating a new one via `GetCombinedStream()`. The new call re-evaluates: if the seek target is within the ring buffer window → new handle attaches. If not → unbuffered fallback.

**Dispose:**

1. Decrement `_entry._readerCount` (Interlocked)
2. If reader count hits 0 → entry transitions to GracePeriod, pump pauses, grace timer starts
3. Handle releases its reference — does not touch the entry's BufferedSegmentStream

## Integration with Existing Code

### NzbFileStream.GetCombinedStream() — modified

Current flow:
```
1. Check shouldUseBufferedStreaming
2. If yes → TryAcquireSlot() → BufferedSegmentStream
3. Else → unbuffered sequential CombinedStream
```

New flow:
```
1. Check shouldUseBufferedStreaming
2. If yes AND DavItemId is available:
   a. SharedStreamManager.TryAttach(davItemId, startPosition)
      → if handle returned: wrap in CombinedStream, done
   b. SharedStreamManager.GetOrCreate(davItemId, factory)
      → if handle returned: wrap in CombinedStream, done
      → if null (no slot): fall through
3. Unbuffered sequential CombinedStream (existing code, unchanged)
```

### NzbFileStream.ReadAsync() — premature EOF handling

If inner stream returns 0 but `_position < _fileSize`: dispose inner stream, set `_innerStream = null`. Next read recreates via `GetCombinedStream()`, which re-evaluates sharing vs unbuffered. This handles the "reader fell behind ring buffer window" detach case.

### DavItemId availability

The `ConnectionUsageContext` already carries `DavItemId` for streaming requests (set in `DatabaseStoreNzbFile.GetStreamAsync()`). NzbFileStream has `_usageContext`, so it reads `_usageContext.DetailsObject?.DavItemId`. No new plumbing needed.

### What doesn't change

- **BufferedSegmentStream internals** — completely untouched
- **DatabaseStoreNzbFile** — still creates NzbFileStream the same way
- **GetAndHeadHandlerPatch** — still creates a stream per HTTP request
- **Queue processing** — `ConnectionUsageType.Queue` already skips buffered streaming, skips the manager too
- **Global semaphore cap** (`TryAcquireSlot`) — still works, now acquired by SharedStreamEntry instead of directly by NzbFileStream

### Connection pool impact

Before: 4 Stremio requests → up to 4 BufferedSegmentStreams → 4 × 20 connections = pool saturated

After: 4 Stremio requests → 1 BufferedSegmentStream (shared) + up to 3 unbuffered sequential (1 connection each) = 23 connections max. Probe requests' unbuffered streams die within 1-2 seconds, leaving just the shared stream's ~20 connections for the real playback.

## Failure Handling

### Graceful degradation (zero-fill) — transparent

BufferedSegmentStream already zero-fills missing segments and marks the DavItem corrupted. The pump reads zero-filled bytes just like real data and writes them into the ring buffer. All attached readers get the same zero-filled data. The corruption flag gets set once. This path is completely transparent to the sharing layer.

### Unrecoverable failure

BufferedSegmentStream throws (`InvalidDataException` or `PermanentSegmentFailureException`). The pump catches this:

1. Sets `_failure` to the caught exception
2. Sets `_state = Failed`
3. Signals `_dataAvailable` — wakes any blocked readers
4. Calls `SharedStreamManager.Evict(davItemId)` — removes entry immediately
5. Does NOT start grace period — failed entries die instantly
6. Disposes BufferedSegmentStream, releases semaphore slot

All currently attached handles throw the failure exception on their next `ReadAsync`. NzbFileStream propagates this to the HTTP layer — same behavior as today.

### New request after failure

The failed entry was evicted, so `TryAttach()` returns null. `GetOrCreate()` creates a fresh entry with a new BufferedSegmentStream. If the same segments are still missing, the same failure recurs — identical to current behavior.

### Race conditions

- `_readerCount` managed with `Interlocked` operations
- `_state` transitions are guarded — `Failed` takes priority over `GracePeriod`
- Reader disposing during pump failure: reader just decrements count; pump's eviction handles cleanup
- Grace period active when pump fails: pump sets `_state = Failed`, overrides GracePeriod, cancels grace timer, evicts immediately

## Configuration

Two new config keys:

### `usenet.shared-stream-grace-period`

Integer, seconds. How long a shared entry stays alive after the last reader disconnects.

**Default: 10**

- Bridges Stremio's probe-to-stream gap (typically 1-2s)
- Covers pause/scrub reconnects
- Short enough to not hoard semaphore slots on abandoned files

Exposed via `ConfigManager.GetSharedStreamGracePeriod()`.

### `usenet.shared-stream-buffer-size`

Integer, megabytes. Size of the ring buffer per shared entry.

**Default: 15**

- Matches ~20 segments × 750KB (same order as BufferedSegmentStream's internal buffer)
- Worst case with concurrent stream cap of 2: 2 × 15MB = 30MB ring buffer memory
- Users with faster connections who see detach issues can increase
- Minimum: 2MB

Exposed via `ConfigManager.GetSharedStreamBufferSize()`.

### No enable/disable toggle

The feature activates automatically when buffered streaming is enabled (`usenet.use-buffered-streaming = true`). The existing `usenet.max-concurrent-buffered-streams` cap still applies — SharedStreamManager acquires slots through the same `TryAcquireSlot()` mechanism.

## New Files

- `backend/Streams/SharedStreamManager.cs` — static manager class
- `backend/Streams/SharedStreamEntry.cs` — entry with pump, ring buffer, lifetime
- `backend/Streams/SharedStreamHandle.cs` — per-reader Stream implementation

## Modified Files

- `backend/Streams/NzbFileStream.cs` — `GetCombinedStream()` checks manager first; `ReadAsync()` handles premature EOF
- `backend/Config/ConfigManager.cs` — two new getter methods
