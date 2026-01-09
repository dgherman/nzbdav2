# Optimization Report: High-Latency Streaming

## Objective
Optimize NzbDav sequential read throughput under hostile network conditions (150ms latency, 40ms jitter, 1% packet loss/stall).

## Methodology
- **Benchmark:** `FullNzbTester` with `MockNntpServer`.
- **Conditions:** 16 Connections, 150ms Latency, 40ms Jitter, 1% Stall (10s delay).
- **Baseline:** ~16 MB/s median throughput.

## Optimizations Tested

### Optimization 1: Aggressive Straggler Detection (0.6s)
- **Change:** Reduced straggler detection threshold from 1.5s to 0.6s.
- **Result:** ~44 MB/s median.
- **Verdict:** Good improvement, but high connection churn.

### Optimization 2: UsenetSharp Fixes (Cancellation Support)
- **Change:** Modified `UsenetSharp` to respect `CancellationToken` in `ReadAsync`.
- **Result:** ~1.2 MB/s.
- **Verdict:** **FAILURE**. Caused connection poisoning because cancelled tasks didn't drain the stream, leaving the socket in an invalid state for the next request.

### Optimization 3: Large Buffer + 1.5s Threshold
- **Change:** Increased prefetch buffer from 80 to 160 segments (~112MB). Kept conservative 1.5s threshold.
- **Result:** ~45 MB/s median. Peak 54 MB/s.
- **Verdict:** **SUCCESS**. Best stability and throughput. The large buffer absorbs the 1.5s delay during stalls, preventing starvation.

### Optimization 4: Large Buffer + 0.8s Threshold
- **Change:** Same as Opt 3 but with 0.8s threshold.
- **Result:** ~43 MB/s median.
- **Verdict:** Comparable to Opt 3 but slightly more churn.

### Optimization 5/6: "Kill-and-Race" Strategy
- **Change:** Try to kill the stalled connection to free up a pool slot for the racer.
- **Result:** 0-17 MB/s.
- **Verdict:** **FAILURE**. Killing connections caused pool starvation because `UsenetSharp` tasks (zombies) didn't release sockets immediately, blocking new connections.

## Final Configuration
- **Buffer Size:** 160 Segments (up from ~80).
- **Straggler Threshold:** 1.5 Seconds.
- **Priority Queue:** Fixed bug where urgent (raced) segments were deprioritized.
- **UsenetSharp:** Reverted to original stable version.

## Key Learnings
1.  **Buffering is King:** In high-latency/jitter environments, a large prefetch buffer (covering > 2s of playback) is more effective than aggressive re-trying.
2.  **Connection Safety:** Cancelling active NNTP commands is dangerous. It's safer to let them drain or timeout naturally than to risk poisoning the pool.
3.  **Pool Starvation:** "Killing" a task doesn't always free the resource immediately.

## Next Steps
- Consider implementing "Speculative Pipelining" in `UsenetSharp` for lower latency.
- Investigate `TCP_QUICKACK` or socket tuning for the Mock Server to reduce stall impact.
