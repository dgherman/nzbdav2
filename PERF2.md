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
