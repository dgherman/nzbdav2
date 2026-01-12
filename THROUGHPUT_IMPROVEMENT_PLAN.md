# Sequential Throughput Improvement Plan

**Current Performance:** 11.17 MB/s with 50ms mock latency
**Target:** 50+ MB/s under same conditions

## Architecture Analysis

### Current Data Flow
```
┌─────────────────┐    ┌──────────────────┐    ┌───────────────────┐
│  NNTP Server    │───>│  Worker Tasks    │───>│  Buffer Channel   │───> Consumer
│  (50ms latency) │    │  (N parallel)    │    │  (Bounded Queue)  │
└─────────────────┘    └──────────────────┘    └───────────────────┘
                              │
                              ▼
                       ┌──────────────────┐
                       │  Full Segment    │
                       │  Read to Memory  │
                       │  + yEnc Decode   │
                       └──────────────────┘
```

### Bottlenecks (Ordered by Impact)

1. **Sequential Buffer Write Ordering** (CRITICAL)
   - Workers must wait on `writeLock` semaphore to write segments in order
   - If segment N+1 finishes before segment N, it waits in `fetchedSegments` dictionary
   - Creates head-of-line blocking - fast workers blocked by slow ones

2. **Full Segment Read Before Buffering** (HIGH)
   - Each segment (~700KB) is fully read and decoded before adding to buffer
   - Consumer cannot start reading until entire segment is in memory
   - Creates ~50ms minimum latency per segment

3. **Single Writer Channel Constraint** (MEDIUM)
   - `SingleWriter = true` in BoundedChannelOptions
   - Forces serialization even though we already have write lock

4. **Per-Segment Connection Acquisition** (MEDIUM)
   - Each segment fetch acquires/releases connection from pool
   - Overhead accumulates over 147+ segments

---

## Implementation Plan (Re-ordered by Priority)

### Priority 1: Lock-Free Segment Ordering
**Impact:** HIGH - Eliminates head-of-line blocking
**Effort:** Medium
**File:** `backend/Streams/BufferedSegmentStream.cs`

**Problem:** Current implementation uses `writeLock` semaphore + `fetchedSegments` dictionary:
```csharp
// Current (lines 304-316)
await writeLock.WaitAsync(ct);
try {
    while (fetchedSegments.TryRemove(nextIndexToWrite, out var orderedSegment)) {
        await _bufferChannel.Writer.WriteAsync(orderedSegment, ct);
        nextIndexToWrite++;
    }
} finally {
    writeLock.Release();
}
```

**Solution:** Pre-allocate slot array, workers write directly, consumer polls in order:

```csharp
// New approach
private readonly PooledSegmentData?[] _segmentSlots;
private volatile int _nextConsumeIndex = 0;
private volatile int _highestFetchedIndex = -1;

// Constructor
_segmentSlots = new PooledSegmentData?[totalSegments];

// Worker writes directly to slot (lock-free):
Interlocked.Exchange(ref _segmentSlots[job.index], segmentData);
Interlocked.CompareExchange(ref _highestFetchedIndex, job.index,
    _highestFetchedIndex < job.index ? _highestFetchedIndex : job.index);

// Consumer reads in order (single reader, no lock needed):
while (_segmentSlots[_nextConsumeIndex] != null)
{
    var segment = Interlocked.Exchange(ref _segmentSlots[_nextConsumeIndex], null);
    if (segment != null)
    {
        await _bufferChannel.Writer.WriteAsync(segment, ct);
        _nextConsumeIndex++;
    }
}
```

**Benefits:**
- No write lock contention
- Workers never block on each other
- O(1) slot access vs dictionary operations

---

### Priority 2: Remove SingleWriter Constraint
**Impact:** LOW-MEDIUM
**Effort:** Low
**File:** `backend/Streams/BufferedSegmentStream.cs:66-71`

```csharp
// Current
var channelOptions = new BoundedChannelOptions(bufferSegmentCount)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,
    SingleWriter = true  // <-- Unnecessary constraint
};

// Fixed
var channelOptions = new BoundedChannelOptions(bufferSegmentCount)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,
    SingleWriter = false  // Allow parallel writes
};
```

**Note:** With Priority 1 implemented, this becomes less critical since a single ordering task would write to channel. But if keeping current architecture, removing this helps.

---

### Priority 3: Connection Affinity (Worker-Connection Binding)
**Impact:** MEDIUM - Reduces per-segment overhead
**Effort:** Medium
**File:** `backend/Streams/BufferedSegmentStream.cs` (worker loop)

**Problem:** Each segment fetch does:
1. Acquire connection from pool
2. Send ARTICLE command
3. Read response
4. Release connection

**Solution:** Workers acquire connection once, reuse for batch:

```csharp
// Current worker loop (simplified)
while (TryGetJob(out var job))
{
    var stream = await client.GetSegmentStreamAsync(job.segmentId, ...);
    // Process
}

// Improved - connection affinity
var connection = await AcquireDedicatedConnection(ct);
try
{
    while (TryGetJob(out var job))
    {
        // Reuse same connection for multiple segments
        var stream = await connection.GetSegmentStreamAsync(job.segmentId, ...);
        // Process
    }
}
finally
{
    ReleaseConnection(connection);
}
```

**Benefits:**
- Eliminates pool acquire/release overhead per segment
- Better TCP connection utilization
- Reduces context switches

---

### Priority 4: Streaming Decode Pipeline
**Impact:** VERY HIGH - Fundamental architecture improvement
**Effort:** High
**File:** New `PipelinedSegmentStream.cs`

**Problem:** Consumer must wait for full segment decode:
```
Fetch (50ms) -> Decode (5ms) -> Buffer -> Consumer waits 55ms minimum
```

**Solution:** Use `System.IO.Pipelines` for streaming decode:
```
Fetch ─┬─> Decode Chunk 1 ─> Consumer reads immediately
       ├─> Decode Chunk 2 ─> Consumer continues
       └─> Decode Chunk N ─> Consumer finishes
```

**Implementation Sketch:**
```csharp
public class PipelinedSegmentFetcher
{
    private readonly Pipe _pipe = new Pipe();

    public async Task FetchAsync(string segmentId, CancellationToken ct)
    {
        var writer = _pipe.Writer;

        await using var stream = await _client.GetRawSegmentStreamAsync(segmentId, ct);

        while (true)
        {
            var memory = writer.GetMemory(4096);
            var bytesRead = await stream.ReadAsync(memory, ct);
            if (bytesRead == 0) break;

            // Decode yEnc in-place as bytes arrive
            var decoded = YencDecoder.DecodeChunk(memory.Span.Slice(0, bytesRead));
            writer.Advance(decoded);
            await writer.FlushAsync(ct);  // Consumer can read immediately
        }

        writer.Complete();
    }

    public PipeReader Reader => _pipe.Reader;
}
```

**Benefits:**
- Consumer starts reading before segment fully downloaded
- Reduces per-segment latency from 55ms to ~5ms (first chunk)
- Better memory efficiency (no full segment buffering)

---

### Priority 5: Prefetch Hinting for Seeks
**Impact:** HIGH for seek latency (not sequential throughput)
**Effort:** Medium
**File:** `backend/Streams/BufferedSegmentStream.cs`

**Problem:** On seek, buffer is invalid, must wait for new segments

**Solution:** Add `HintSeek(long position)` method:
```csharp
public void HintSeek(long targetPosition)
{
    var targetSegment = (int)(targetPosition / _avgSegmentSize);

    // Cancel current fetch beyond target
    // Prioritize segments around target
    for (int i = Math.Max(0, targetSegment - 2); i <= targetSegment + 5; i++)
    {
        if (!_fetchedSegments.ContainsKey(i))
        {
            _urgentChannel.Writer.TryWrite((i, _segmentIds[i]));
        }
    }
}
```

---

## Implementation Order

| Priority | Change | Expected Impact | Effort |
|----------|--------|-----------------|--------|
| **P1** | Lock-free segment ordering | +50-100% throughput | Medium |
| **P2** | Remove SingleWriter | +5-10% throughput | Low |
| **P3** | Connection affinity | +10-20% throughput | Medium |
| **P4** | Streaming decode pipeline | +100-200% throughput | High |
| **P5** | Prefetch hinting | Seek latency only | Medium |

## Expected Results

| Implementation | Throughput |
|----------------|------------|
| Current | 11.17 MB/s |
| + P1 (Lock-free) | ~17-22 MB/s |
| + P2 (SingleWriter) | ~18-24 MB/s |
| + P3 (Affinity) | ~22-30 MB/s |
| + P4 (Pipeline) | ~45-60 MB/s |

---

## Quick Win Test

Implement P1 + P2 first (can be done in ~30 minutes):

1. Replace `writeLock` + `fetchedSegments` with slot array
2. Change `SingleWriter = false`
3. Run benchmark

```bash
dotnet run -- --test-full-nzb --mock-server --latency=50 --size=200
```

Would you like me to implement P1 (lock-free ordering) now?
