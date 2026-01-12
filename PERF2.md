# Performance Analysis: FullNzbTester with Mock Server

**Date:** 2026-01-13
**Test Configuration:**
- Mock NNTP Server: localhost:1190
- Latency: 50ms base, 10ms jitter
- File Size: 100MB (147 segments @ 700KB each)
- Connections: 20 (max), using buffered streaming

## Test Results Summary

```
═══════════════════════════════════════════════════════════════
  FULL NZB TESTER RESULTS SUMMARY
═══════════════════════════════════════════════════════════════
  File Processed:       mock.nzb
  Total Files:          1
───────────────────────────────────────────────────────────────
  SCRUBBING LATENCY (Seek + First Read):
    Seek to 10%:           2284 ms (!)
    Seek to 50%:           9509 ms (!!)
    Seek to 90%:          10743 ms (!!)
    Seek to 20%:           1865 ms (!)
    Total Scrub Time:    24.41 s
───────────────────────────────────────────────────────────────
  SEQUENTIAL THROUGHPUT:
    Speed:               11.17 MB/s
═══════════════════════════════════════════════════════════════
```

### Read Statistics
- Total Reads: 393
- Avg Read Time: 22.34ms
- Median Read Time: 0.05ms
- Min/Max Read Time: 0.02ms / 92.62ms
- P95 Read Time: 69.62ms

## Bugs Fixed During Analysis

### 1. BENCHMARK Environment Variable Not Set (Critical)
**Impact:** Step 1 took 96 seconds instead of 0.5 seconds

**Root Cause:** FullNzbTester with `--mock-server` wasn't setting `BENCHMARK=true`, causing `FetchFirstSegmentsStep` to run full smart analysis on all 147 segments instead of skipping it.

**Fix:** Added `Environment.SetEnvironmentVariable("BENCHMARK", "true");` in FullNzbTester when using mock server.

**File:** `backend/Tools/FullNzbTester.cs:80`

### 2. Stream Reused After CopyToAsync (Bug)
**Impact:** Step 6 (Scrubbing) failed with "Cannot access a disposed object"

**Root Cause:** The stream created for ffprobe analysis (Step 5) was fully consumed by `CopyToAsync()` and then reused for seeking tests in Step 6.

**Fix:** Create a fresh `DavMultipartFileStream` for Step 6 scrubbing tests.

**File:** `backend/Tools/FullNzbTester.cs:372-382`

### 3. CombinedStream StreamTask Cache Not Reset (Critical Bug)
**Impact:** Second seek failed with "Cannot access a disposed object. Object name: 'NzbWebDAV.Streams.NzbFileStream'"

**Root Cause:** When `CombinedStream` disposes a stream (either via `CacheStream` with `maxCachedStreams=0` or when a part is exhausted), the `StreamPart._streamTask` cache wasn't reset. The next `GetStreamTask()` call would return the same completed task containing the disposed stream.

**Fix:**
1. Added `ResetStreamTask()` method to `StreamPart` class
2. Call `ResetStreamTask()` when disposing streams in:
   - `CacheStream()` when `maxCachedStreams <= 0`
   - `ReadAsync()` when a part is exhausted

**File:** `backend/Streams/CombinedStream.cs:438-441, 131-132, 507-514`

## Performance Bottlenecks Identified

### 1. High Seek Latency (2-10 seconds)
**Observation:** Seek + first read takes 2-10 seconds depending on position.

**Analysis:**
- Seek to 10% (10MB): ~2.3s
- Seek to 50% (50MB): ~9.5s
- Seek to 90% (90MB): ~10.7s
- Seek to 20% (20MB): ~1.9s (faster because stream infrastructure was warmed up)

**Root Causes:**
1. **Connection Establishment:** Each seek creates new connections to the NNTP server
2. **Buffer Fill Time:** BufferedSegmentStream needs to prefetch segments
3. **Seek within segment:** After locating the correct segment, needs to read/discard bytes to reach exact position

**Potential Improvements:**
- Pre-warm connections before seeking
- Implement seek-ahead hinting to start buffering target segment before read
- Cache recently accessed segments for backward seeks

### 2. Sequential Throughput Limited by Latency
**Observation:** 11.17 MB/s throughput with 50ms latency

**Analysis:**
- With 700KB segments and 50ms latency per request
- Maximum theoretical single-connection: 700KB / 0.05s = 14 MB/s
- With 20 connections in pipeline, theoretical max: 280 MB/s
- Actual: 11.17 MB/s (4% of theoretical)

**Root Causes:**
1. **Serialization overhead:** yEnc decoding, stream wrapping
2. **Buffer management:** Context switches between producer/consumer
3. **Test overhead:** Progress logging, statistics collection

### 3. Read Time Distribution (Bimodal)
**Observation:** Median 0.05ms but P95 69.62ms

**Analysis:**
- Most reads (median) are served from buffer instantly (0.05ms)
- Every ~20th read (P95) requires waiting for buffer refill (70ms)
- This indicates buffer is sized well but refill causes noticeable hiccups

**Potential Improvements:**
- Increase buffer ahead threshold to start refill earlier
- Add adaptive prefetch based on read patterns

## Configuration Recommendations

### For Low Latency Seeking
```csharp
// Increase stream cache in DavMultipartFileStream
return new CombinedStream(parts, maxCachedStreams: 3);  // Instead of 0
```
This keeps recently accessed parts cached, avoiding recreation on backward seeks.

### For Higher Throughput
```csharp
// In ConfigManager or settings
usenet.connections-per-stream = 30  // Increase from 20
webdav.stream-buffer-segments = 100  // Increase buffer size
```

### For Production Usenet Providers
The mock server uses 50ms latency. Real providers typically have:
- Same datacenter: 5-20ms
- Cross-region: 50-150ms
- International: 100-300ms

Expect performance to scale roughly linearly with latency improvements.

## Test Commands

```bash
# Run mock server benchmark (quick test)
cd backend
export CONFIG_PATH=/opt/docker_local/nzbdav/config
dotnet run -- --test-full-nzb --mock-server --size=100

# Run with lower latency (stress test)
dotnet run -- --test-full-nzb --mock-server --latency=10 --jitter=5 --size=200

# Run with higher latency (simulate distant provider)
dotnet run -- --test-full-nzb --mock-server --latency=150 --jitter=30 --size=100
```

## Summary

| Metric | Before Fixes | After Fixes |
|--------|-------------|-------------|
| Step 1 Duration | 96.0s | 0.5s |
| Step 6 (Scrubbing) | FAILED | 24.4s |
| Sequential Throughput | N/A | 11.17 MB/s |
| All Seeks Complete | No | Yes |

The fixes dramatically improved test reliability. Further performance optimizations are possible but require more invasive changes to the buffering and connection management systems.

---

# Throughput Improvement Plan

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

## Implementation Plan (Ordered by Priority)

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
| + P1 + P2 | ~18-24 MB/s |
| + P1-P3 | ~22-30 MB/s |
| + P1-P4 (Full) | ~45-60 MB/s |
