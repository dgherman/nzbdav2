# Recommended Optimizations for NzbDav

Based on performance analysis of `FullNzbTester` running on a 2.4GB MKV file (Babylon 5), the following optimizations are recommended to improve the user experience, particularly during seeking/scrubbing.

## 1. Optimize `BufferedStream` Worker Cancellations (Critical)
**Observation:** Seeking currently triggers multiple worker cancellations that take ~300ms-600ms each, accumulating to 10s-20s of "Read Time" latency when jumping to new positions.
- **Action:** Improve the `CancellationToken` propagation in `BufferedStream.cs`. Ensure that workers check for cancellation more frequently and exit immediately without waiting for outstanding NNTP requests if possible.
- **Action:** Reduce the logging verbosity of "Worker canceled" warnings to `Debug` or `Trace` to avoid console I/O overhead during rapid seeking.

## 2. Implement Predictive Pre-fetching (High)
**Observation:** Seeking to 90% was faster (4.3s) than seeking to 10% (20.3s), possibly due to the way parts are indexed or buffered.
- **Action:** When a seek occurs, prioritize the download of the immediate segment required for the new position.
- **Action:** Implement a small "seek buffer" that fetches the first few KB of the target segment with high priority, while background workers fill the rest of the look-ahead buffer.

## 3. Parallelize RAR Metadata Extraction (Medium)
**Observation:** PAR2 descriptor parsing takes ~13s. While not a blocker for streaming (as it's a one-time cost), it delays the initial start of the video.
- **Action:** Investigate if `Par2.cs` can parse file descriptors in parallel if multiple PAR2 volumes are available.

## 4. NNTP Connection Warm-up (Low)
**Observation:** Initial probe takes ~17s.
- **Action:** Proactively open a few NNTP connections when an NZB is added to the queue, so they are ready when the deobfuscation steps begin.

## 5. Database Query Optimization (Low)
**Observation:** With a 2.4GB database, `ConfigItems` and `NzbProviderStats` lookups should be as fast as possible.
- **Action:** Ensure indexes exist on `DavItem.Path` and `NzbProviderStats.ProviderId`. (Note: PRAGMA optimizations are already implemented in `Program.cs`).
