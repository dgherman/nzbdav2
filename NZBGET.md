# NZBGet vs NzbDav: Performance Analysis & Architecture Comparison

**Date:** 2026-01-09
**Baseline:** NzbDav Phase 3 (Chunk-based StreamReader) achieving **4.33 MB/s** (Seq)
**Target:** NZBGet performance at **25 MB/s**

## Executive Summary

Through detailed profiling with `FullNzbTester` and instrumented `BufferedSegmentStream`, we have identified the definitive bottleneck:
- **✅ Application overhead is ZERO** - 100% of time in `ReadAsync`
- **✅ Parallelism is working** - We successfully buffer 150+ segments ahead of the reader.
- **❌ Head-of-Line Blocking** - throughput is destroyed by single slow segments.
- **Critical Finding:** We often have 100MB+ of data buffered (150 segments), but the reader is starved waiting for *one* specific segment (e.g., segment #3) to complete before it can access segments #4-#150.

## Benchmark Results (Jan 9, 2026)

**Test File:** `YG7NrjDbpf1DhhrvQulbaZ5tLtJLdkmf.mkv` (1 GB)

| Strategy | Throughput | Max Read Time | Notes |
| :--- | :--- | :--- | :--- |
| **Baseline (Phase 3)** | 4.33 MB/s | 6378ms | Severe stalling (6.4s) |
| **Straggler Detect (3s)** | 5.56 MB/s | 1910ms | +28% speedup |
| **Straggler Detect (1.5s)** | **11.64 MB/s** | **1052ms** | **+168% speedup** |

**Interpretation:**
Reducing the straggler detection threshold from 3s to **1.5s** yielded a massive performance gain. The system now aggressively "races" any segment that blocks the head of the line for more than 1.5 seconds, effectively neutralizing slow connections.

## Root Cause: Head-of-Line Blocking

The current `BufferedSegmentStream` architecture:
1.  **Producer:** Queues all 1500 segments.
2.  **Workers (20):** Pick segments indiscriminately.
3.  **Ordering:** Segments must be written to the output channel **strictly in order**.
4.  **The Flaw:** If Worker A picks Segment 100 and finishes instantly, it waits in `fetchedSegments` dictionary. If Worker B picks Segment 99 and takes 10 seconds (slow provider/connection), the stream stalls for 10 seconds. Even though we have Segment 100 ready, we can't emit it.

**Comparison with NZBGet:**
NZBGet likely handles this by:
1.  **Aggressive Timeout/Kill:** If a connection is slow on a high-priority segment, it kills it or races another connection.
2.  **Prioritized Assignment:** It assigns the "earliest" missing segments to the fastest available connections.

## Optimization Plan

### 1. Speculative/Redundant Fetching (The "Straggler Killer")
**Problem:** One slow connection kills throughput.
**Solution:**
- Monitor the "Next Index Needed" (`_nextIndexToRead`).
- If `_nextIndexToRead` is being processed by a worker for > X seconds (or is significantly slower than peers), **trigger a duplicate fetch** for that same segment on a different (free) connection.
- First one to finish wins.

### 2. Priority Queue for Workers
**Problem:** Workers pick random segments from the future (e.g. segment 1000) while segment 5 is pending.
**Solution:**
- Ensure workers always pick the *lowest available index* that isn't being processed.
- (Current implementation using `Channel` already does this roughly, as items are written in order. But if a worker fails/retries, it might get out of sync).
- *Correction:* The current `segmentQueue` is ordered. Workers pick 0, 1, 2... in order. The issue is purely that Worker for #99 is slow.

### 3. Dynamic Timeout / "Give Up" Strategy
**Problem:** We wait 60s+ for a connection to time out.
**Solution:**
- If a segment is the "Head of Line" (blocking the stream), reduce its timeout drastically (e.g. 5s).
- If it fails/times out, put it back in the high-priority queue.

### 4. Connection Scoring
**Problem:** We assign critical Head-of-Line segments to potentially slow providers.
**Solution:**
- Track speed of each provider.
- Assign `_nextIndexToRead` ONLY to top-tier providers.

## Implementation Steps (Next Iteration)

1.  **Implement "Straggler Detection" in `BufferedSegmentStream`:**
    - Background task that watches `_nextIndexToRead`.
    - If `_nextIndexToRead` stays same for > 2 seconds AND we have buffered segments ahead of it:
        - Identify which worker/provider has `_nextIndexToRead`.
        - **Cancel that worker** (or duplicate the job).
        - Re-queue the segment with high priority.

2.  **Tune Timeouts:**
    - Ensure `FetchSegmentWithRetryAsync` doesn't wait forever on a bad connection.

## Legacy Notes (Previous Attempts)
- **Streaming Chunks:** Failed (overhead > benefit).
- **Socket Tuning:** Valid, but secondary to the blocking issue.
- **Connection Pool:** Valid, helped stability, but didn't solve the blocking.

**Current Goal:** 25 MB/s
**Strategy:** Eliminate 10s stalls by killing slow connections aggressively.
