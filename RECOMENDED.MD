# Recommended Optimizations for NzbDav

Based on performance analysis of `FullNzbTester` running on a 2.4GB MKV file (Babylon 5), the following optimizations are recommended to improve the user experience, particularly during seeking/scrubbing.

## Baseline Performance Stats (Babylon 5 - 2.4GB MKV)
These stats represent the performance **after** optimizing worker cancellations (Iteration 1).

| Phase | Previous (Baseline) | Current (Optimized) | Improvement |
|-------|-------------------|-------------------|-------------|
| **Seek to 10%** | 8.5s | **7.1s** | 16% Faster |
| **Seek to 50%** | 4.9s | **4.9s** | - |
| **Seek to 90%** | 2.7s | **2.4s** | 11% Faster |
| **Seek to 20%** | 7.4s | **6.4s** | 13.5% Faster |
| **Total Scrubbing** | 23.5s | **20.8s** | 12% Faster |
| **Seq. Throughput** | 8.84 MB/s | **10.17 MB/s** | 15% Faster |

## 1. Optimize `BufferedStream` Worker Cancellations (In Progress)
**Status:** Initial optimization applied (Log level reduced, aggressive cancellation checks added).
**Observation:** Seek latency improved by ~12-16%, but "Workers have not completed" warning persists, indicating tasks are still blocking during cleanup.
- **Next Action:** Investigate why `Task.WhenAll` hangs even after cancellation. The `INntpClient` might be blocking on socket `Dispose` or `Read` operations that don't respect the token immediately.

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
