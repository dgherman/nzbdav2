# QoS Connection Limits - Behavioral Analysis

## Deployment Status
✅ Build `v2025-11-29-QOS-LIMITS` successfully deployed
✅ OperationLimitedPool is active and logging usage

## Observed Behavior

### Current Connection Usage Patterns

**Recent snapshot (21:48:44):**
```
Total: 149 connections in use (exceeds 145 limit!)
├── Queue: 25 connections (configured: 30 guaranteed)
├── HealthCheck: 60 connections (configured: 60 maximum) ✅ HONORED
└── BufferedStreaming: 64 connections (configured: 55 guaranteed)
```

**Historical patterns from logs:**
- Queue usage: Varies between 1-25 connections (never reaches 30)
- HealthCheck usage: **Consistently capped at 60** ✅ Working as intended
- BufferedStreaming usage: 64-107 connections (significantly exceeds 55 guarantee)

### Key Findings

#### ✅ What's Working:
1. **HealthCheck hard limit is enforced correctly**
   - Always stays at or below 60 connections
   - QoS semaphore successfully prevents exceeding the limit

2. **Connection pool tracking is accurate**
   - OperationLimitedPool correctly tracks usage by type
   - Logs show proper release/request cycles

#### ❌ Critical Issues Identified:

### Issue #1: Total Connection Count Exceeds Pool Size
**Problem:** 149 connections active when pool size is 145

**Evidence:**
```
Usage=Queue=25,HealthCheck=60,BufferedStreaming=64
Total: 25 + 60 + 64 = 149 > 145 (configured pool size)
```

**Root Cause Analysis:**
The inner `ConnectionPool<T>` shows:
- `Live=60` (physical connections created)
- `RemainingSemaphore=36` (slots available at inner pool)
- But OperationLimitedPool tracking shows 149 total usage

This discrepancy indicates:
1. **Multiple providers are in play** - We're seeing TWO separate pools:
   - Provider 1: `Live=60, Active=60` (fully utilized)
   - Provider 2: `Live=16, Active=16` (smaller pool)

2. **Per-provider pool sizing issue** - Each provider has its own ConnectionPool, and the OperationLimitedPool limits are applied per-provider, not globally

3. **The 145 limit is per-provider, not system-wide** - With 2 providers, actual max could be 290 connections!

### Issue #2: Queue Not Using Allocated Capacity
**Problem:** Queue only using 25 connections despite 30 being guaranteed and queue having many items

**Evidence:**
```
Queue usage observed: 1-25 connections (historically 2-18 most common)
Queue guarantee: 30 connections
User reports: "queue has many items but not using connections"
```

**Potential Causes:**
1. **Single-threaded queue processor** - QueueManager processes one item at a time, so it won't use 30 concurrent connections unless a single item needs them

2. **Queue items may be small** - If NZB files are small, each queue item completes quickly without needing many concurrent segment downloads

3. **BufferedStreaming stealing capacity** - Even though Queue has 25 semaphore slots available (out of 30), the inner pool might be exhausted by BufferedStreaming's 64 connections

4. **Inner pool contention** - The logs show:
   ```
   Available=36, RemainingSemaphore=36
   ```
   When Queue tries to use more than 25, it may find the inner pool saturated by the 64 BufferedStreaming connections

### Issue #3: BufferedStreaming Exceeds Guaranteed Limit
**Problem:** BufferedStreaming using 64+ connections when guaranteed only 55

**Evidence:**
```
BufferedStreaming usage: 64-107 connections (varies widely)
BufferedStreaming guarantee: 55 connections
```

**Root Cause:**
This is actually **correct QoS behavior** (ballooning), BUT the issue is:
1. BufferedStreaming is ballooning into Queue's unused capacity (Queue using only 25/30)
2. This is preventing Queue from ramping up when needed
3. The semaphore enforces the *guaranteed minimum*, but doesn't prevent ballooning

However, the excessive ballooning (64-107) suggests:
- Multiple streams are active concurrently
- Each stream can use up to 5 connections (per-stream config)
- With 12-20+ active streams, this explains the high BufferedStreaming count

## Architecture Insight: Multi-Provider Pools

### Current Architecture (Discovered)
```
UsenetStreamingClient
├── Provider 1 (145 connection pool)
│   └── OperationLimitedConnectionPool (Queue=30, HealthCheck=60, Streaming=55)
│       └── ConnectionPool (145 total)
├── Provider 2 (145 connection pool)
│   └── OperationLimitedConnectionPool (Queue=30, HealthCheck=60, Streaming=55)
│       └── ConnectionPool (145 total)
└── Provider N...
```

**Implication:**
- Each provider has its own OperationLimitedPool
- Limits are per-provider, not global
- With 2 providers: theoretical max = 290 connections (2 × 145)
- Current observation: 149 connections across both providers

### Why Queue Isn't Using Connections

**The Real Bottleneck:**
Queue is using 25 connections, which seems low, but this might be correct because:

1. **Single-threaded queue processing** - Only one NZB is processed at a time
   - Each NZB file is processed sequentially
   - Connection usage depends on file structure (number of segments, whether it needs deobfuscation, etc.)

2. **Per-item parallelism limit** - The 30 Queue connections are meant to be used within a single queue item, not across multiple items
   - If current queue item only has 25 segments being downloaded in parallel, that's the natural limit
   - The item would need 30+ concurrent segment downloads to use all 30 connections

3. **Queue vs. Streaming competition** - The real issue:
   ```
   Inner Pool: Live=60, Active=60, Available=36
   Queue wants more connections (has 5 unused semaphore slots)
   BUT inner pool may be saturated by BufferedStreaming
   ```

## Expected vs. Actual Behavior

### Expected (QoS Design):
```
Idle system:
- Queue processing: Can use up to 145 connections (balloons)
- HealthCheck: Uses 0
- BufferedStreaming: Uses 0

Active system (Queue + HealthCheck):
- Queue: Guaranteed 30, can balloon to 85 if HealthCheck idle
- HealthCheck: Guaranteed 60, hard capped at 60
- BufferedStreaming: Guaranteed 55, can balloon into unused Queue capacity

Active system (All operations):
- Queue: Gets at least 30 (guarantee)
- HealthCheck: Gets at least 60 (guarantee, also hard max)
- BufferedStreaming: Gets at least 55 (guarantee)
- Total: 145 (all guarantees satisfied)
```

### Actual (Observed):
```
Current state:
- Queue: 25 (below guarantee of 30) ⚠️
- HealthCheck: 60 (at guarantee/max) ✅
- BufferedStreaming: 64 (ballooned beyond 55 guarantee) ✅ (correct behavior)
- Total: 149 (exceeds 145 pool size) ❌ CRITICAL

Inner pool state:
- Live=60, Active=60 (Provider 1 fully utilized)
- Live=16, Active=16 (Provider 2 partially utilized)
- Total physical: 76 live connections
- Total usage tracked: 149 operations
```

## Root Cause Summary

### The Fundamental Mismatch:
**OperationLimitedPool tracks "usage requests" but doesn't limit physical connections from inner pool**

The semaphores control how many operations of each type can *request* connections simultaneously, but:
1. **Inner pool is separate** - It has 145 physical connections
2. **Ballooning happens at inner pool level** - Operations compete for those 145 connections
3. **Usage tracking counts semaphore acquisitions** - Not actual inner pool usage

### Why Queue Appears Starved:
Queue is actually using 25 physical connections (reasonable for single-item processing), but appears "low" because:
1. BufferedStreaming has 64 *tracked* requests (but may not be using 64 physical connections)
2. The inner pool shows only 60 live connections (from Provider 1)
3. **Mismatch between tracked usage (149) and physical connections (76)**

## The Real Problem

### Double-Counting Hypothesis:
Looking at the numbers:
- OperationLimitedPool tracks: Queue=25, HealthCheck=60, BufferedStreaming=64 = 149
- Inner ConnectionPool shows: Live=60 (Provider 1) + Live=16 (Provider 2) = 76 physical connections

**Possibility:** Each operation is being counted twice:
1. Once when acquiring OperationLimitedPool semaphore
2. Again somewhere else (perhaps at BufferedSegmentStream level?)

OR

**Multi-provider aggregation:**
The usage numbers (149) might be aggregated across both providers, while the ConnectionPool logs show per-provider state.

## Questions for Investigation

1. **How many providers are configured?**
   - Are there 2+ Usenet providers?
   - Each provider gets its own 145-connection pool?

2. **Where does Queue processing happen?**
   - Is QueueManager truly single-threaded (one item at a time)?
   - Or can it process multiple queue items concurrently?

3. **What causes the usage/physical mismatch?**
   - Why does OperationLimitedPool show 149 usage when ConnectionPool shows only 76 live?
   - Is this double-counting or multi-provider aggregation?

4. **Is BufferedStreaming correctly limited?**
   - Should it be limited to 55 per-provider or 55 globally?
   - Current behavior (64) suggests per-provider limit + ballooning

## Recommendations for Fix (Analysis Only)

### Potential Fixes (Not Implemented):

1. **Global vs. Per-Provider Limits**
   - Consider whether limits should be global (across all providers) or per-provider
   - Current: Per-provider (each provider has 30/60/55 limits)
   - Might need: Global limits with provider-aware distribution

2. **Clarify Queue Expectations**
   - If Queue is single-threaded by design, 25 connections might be correct
   - If Queue should process multiple items concurrently, need architectural change

3. **Fix Usage Counting Mismatch**
   - Investigate why OperationLimitedPool shows 149 but ConnectionPool shows 76
   - Ensure usage tracking matches physical connection allocation

4. **Consider Per-Stream Limits**
   - BufferedStreaming at 64 = potentially 12+ concurrent streams
   - Each stream limited to 5 connections (configured separately)
   - This seems correct, but competes with Queue

## Conclusion

The QoS-style OperationLimitedPool is **partially working**:
- ✅ HealthCheck hard limit (60) is correctly enforced
- ✅ Per-operation semaphores are functioning
- ✅ Ballooning behavior is working (BufferedStreaming can exceed guarantee)

But there are **critical issues**:
- ❌ Total usage (149) exceeds pool size (145)
- ❌ Usage tracking doesn't match physical connections (149 vs. 76)
- ⚠️ Queue usage (25) is below guarantee (30), but might be architecturally correct
- ⚠️ Multi-provider architecture not accounted for in limits design

**Next Steps:**
1. Clarify multi-provider behavior and whether limits should be global
2. Investigate the usage counting mismatch (149 tracked vs. 76 physical)
3. Determine if Queue's low usage is a problem or expected behavior
4. Consider whether current "ballooning" is too aggressive for BufferedStreaming
