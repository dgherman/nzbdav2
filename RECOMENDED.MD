# Recommended Optimizations for NzbDav

Based on performance analysis of `FullNzbTester` running on a 2.4GB MKV file (Babylon 5), the following optimizations are recommended to improve the user experience, particularly during seeking/scrubbing.

## Baseline Performance Stats (Babylon 5 - 2.4GB MKV)
These stats represent the performance **after** optimizing worker cancellations (Iteration 2 - Stable).

| Phase | Previous (Baseline) | Current (Optimized) | Improvement |
|-------|-------------------|-------------------|-------------|
| **Seek to 10%** | 8.5s | **7.9s** | ~7% Faster |
| **Seek to 50%** | 4.9s | **5.2s** | - |
| **Seek to 90%** | 2.7s | **2.5s** | ~7% Faster |
| **Seek to 20%** | 7.4s | **6.8s** | ~8% Faster |
| **Total Scrubbing** | 23.5s | **22.5s** | ~4% Faster |
| **Seq. Throughput** | 8.84 MB/s | **9.68 MB/s** | ~10% Faster |

## 1. Optimize `BufferedStream` Worker Cancellations (Implemented)
**Status:** Implemented with a 500ms drain timeout.
**Details:** 
- Reduced log verbosity for cancellations to `Debug`.
- Added aggressive cancellation checks in worker loops.
- Implemented a **500ms timeout** for draining connections during disposal. If a connection takes longer than 500ms to finish its current segment, it is forcibly killed and replaced. This prevents large downloads from blocking the seek operation while avoiding the overhead of killing every connection instantly.
**Observation:** Seek latency improved slightly, and sequential throughput increased by ~10%. Log noise is significantly reduced.

## 2. Maximize Sequential Throughput (High)
**Observation:** Seeking currently triggers multiple worker cancellations that take ~300ms-600ms each, accumulating to 10s-20s of "Read Time" latency when jumping to new positions.
- **Action:** Improve the `CancellationToken` propagation in `BufferedStream.cs`. Ensure that workers check for cancellation more frequently and exit immediately without waiting for outstanding NNTP requests if possible.
- **Action:** Reduce the logging verbosity of "Worker canceled" warnings to `Debug` or `Trace` to avoid console I/O overhead during rapid seeking.

## 2. Maximize Sequential Throughput (High)
**Observation:** Current throughput is ~8.7 MB/s, which is ~35% of the potential 25 MB/s.
- **Action:** Investigate `BufferedSegmentStream` bottlenecks. The lock contention on `_bufferChannel` or the overhead of `Memory<byte>` copying might be limiting throughput.
- **Action:** Increase the number of concurrent connections or optimize the `ConnectionPool` acquisition strategy if connections are idling unnecessarily.
- **Action:** Review `AesDecoderStream` (if encryption is used, though not in this test case) and `YencHeaderStream` for CPU-bound bottlenecks.

## 3. Implement Predictive Pre-fetching (High)
**Observation:** Seeking to 90% was faster (4.3s) than seeking to 10% (20.3s), possibly due to the way parts are indexed or buffered.

## 3. Parallelize RAR Metadata Extraction (Medium)
**Observation:** PAR2 descriptor parsing takes ~13s. While not a blocker for streaming (as it's a one-time cost), it delays the initial start of the video.
- **Action:** Investigate if `Par2.cs` can parse file descriptors in parallel if multiple PAR2 volumes are available.

## 4. NNTP Connection Warm-up (Low)
**Observation:** Initial probe takes ~17s.
- **Action:** Proactively open a few NNTP connections when an NZB is added to the queue, so they are ready when the deobfuscation steps begin.

## 5. Database Query Optimization (Low)
**Observation:** With a 2.4GB database, `ConfigItems` and `NzbProviderStats` lookups should be as fast as possible.
- **Action:** Ensure indexes exist on `DavItem.Path` and `NzbProviderStats.ProviderId`. (Note: PRAGMA optimizations are already implemented in `Program.cs`).
