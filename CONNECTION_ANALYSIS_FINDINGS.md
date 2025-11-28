# Connection Pool Analysis Findings

## Current System State (from logs)

### Configuration
- **Total Pooled Connections**: 145
- **Max Queue Connections**: 30
- **Reserved for Non-Queue**: 115
- **Connection Pool**: Live=30, Idle=0, Active=30

### Current Usage Breakdown
From log line: `Usage=Queue=5,HealthCheck=3,BufferedStreaming=22`
- Queue: 5 connections
- HealthCheck: 3 connections
- BufferedStreaming: 22 connections
- **Total Active**: 30 connections

## CRITICAL PROBLEM IDENTIFIED

### The Issue: Impossible Reservation Requirements

The queue is configured with **completely broken reservation logic**:

```
RequiredReserved=115
CurrentCount=115
Queue requests with: RequiredAvailable=115
```

**What this means:**
- The queue waits until `CurrentCount > 115` (needs 116+ free connections)
- But there are only **145 total connections** in the system
- With 30 connections active (Live=30), that leaves **115 free**
- The queue needs **116 free**, but only **115 exist**
- **Result: Queue can BARELY EVER acquire connections!**

### The Math That Proves It's Broken

```
TotalPooledConnections = 145
MaxQueueConnections = 30
ReservedForNonQueue = 145 - 30 = 115

Queue's requiredAvailable = 115
Queue can acquire if: CurrentCount > 115 (needs 116+ free)

But:
- Active connections: 30
- Free connections: 145 - 30 = 115
- Queue needs: 116
- **Gap: 1 connection short!**
```

### Why Queue IS Working (But Barely)

The queue IS getting connections, but **only by luck**:

1. A connection gets returned (CurrentCount becomes 116 temporarily)
2. Queue immediately grabs it (bringing CurrentCount back to 115)
3. Queue process uses it and returns it
4. Repeat

**Evidence from logs:**
```
[ExtSemaphore] Released and granted 1 waiters: CurrentCount=115, Waiters=59
[ConnPool] Connection reused for Queue: Live=30, Idle=0, Active=30
[ConnPool] Connection returned from Queue: Live=30, Idle=0, Active=30
```

The queue is working in a **serialized, one-connection-at-a-time mode** instead of using the 30 connections it's supposed to have access to!

## Root Cause Analysis

### Mismatch Between Connection Pool Size and Total Configuration

The issue is:
- **Total configured connections** (TotalPooledConnections): 145
- **Connection pool actual size**: 30 (Live=30)
- **Queue reservation calculation**: Based on 145, not 30!

```csharp
// QueueItemProcessor.cs:113
var reservedConnections = providerConfig.TotalPooledConnections - concurrency;
// reservedConnections = 145 - 30 = 115

// But the ACTUAL connection pool only has 30 connections!
// So it should be:
// reservedConnections = 30 - 30 = 0
```

### Why This Mismatch Exists

Looking at the configuration:
- **TotalPooledConnections = 145** (this is the sum across ALL providers)
- **Connection Pool Size = 30** (this is per-provider)

The code calculates reservation based on `TotalPooledConnections` (145), but the semaphore operates on the **per-provider pool** (30).

## The Actual Problem

The bug is in how the reservation is calculated. Looking at the logs:

```
[ConnPool] Requesting connection for Queue:... RequiredReserved=115, RemainingSemaphore=65
```

**Wait, RemainingSemaphore=65?**

This reveals the issue:
- The **local provider pool** has 65 connections (not 30!)
- The Live=30 is just the currently active connections
- But the queue is requiring 115 to be kept reserved

Let me re-analyze:
- **Local pool max**: Probably 65 (RemainingSemaphore=65 when nothing is waiting)
- **Currently live**: 30
- **Available**: 65 - 30 = 35? (But logs say Available=65)

Actually, looking more carefully:
- `Available=65` in the logs
- `RemainingSemaphore=65`
- `RequiredReserved=115`

This doesn't add up. Let me look at the actual logic...

## Re-Analysis: The Real Issue

Looking at the debug output more carefully:

```
Available=65, RequiredReserved=115, RemainingSemaphore=65
CurrentCount=115, RequiredAvailable=115
```

**Ah! Now I see it:**

- `CurrentCount=115` is the **GLOBAL** semaphore (ExtendedSemaphoreSlim with 145 total)
- `RemainingSemaphore=65` is the **LOCAL** per-provider semaphore
- The queue is waiting for `CurrentCount > 115` in the global semaphore

**The bottleneck is the GLOBAL semaphore, not the local one!**

### The Design

There are TWO semaphores:
1. **Global ExtendedSemaphoreSlim** (145 slots total)
2. **Local per-provider SemaphoreSlim** (65 slots for this provider)

Both must be acquired via `CombinedSemaphoreSlim`:
```csharp
// CombinedSemaphoreSlim.cs:15
await pooledSemaphore.WaitAsync(requiredAvailable, cancellationToken);
await _semaphore.WaitAsync(cancellationToken);
```

**The problem:**
- Queue requires `requiredAvailable=115` on the GLOBAL semaphore
- With only 30 active connections, there are 115 slots free in global semaphore
- Queue needs **116 free** to acquire one
- **It can BARELY acquire, only when something else releases at exactly the right moment!**

## Why It's Working At All

The queue IS slowly progressing because:

1. **BufferedStreaming connections return periodically**
2. When they return, `CurrentCount` briefly exceeds 115 (goes to 116+)
3. Queue immediately grabs one connection
4. Uses it quickly and returns it
5. Process repeats

**But this is horribly inefficient!** The queue should be able to use 30 connections simultaneously, but instead it's using ~5 at a time (as shown in `Usage=Queue=5`).

## The Fix Required

### Problem: My Earlier Fix Didn't Get Applied!

Looking at the logs, the issue is still present, which means:
1. The code changes I made weren't compiled/deployed
2. OR the container is running the old image

The container is running `local/nzbdav:1`, which is a locally built image. My changes to the source code haven't been rebuilt into the Docker image!

## Immediate Actions Required

1. **Rebuild the Docker image** with the fixed code
2. **OR** adjust the configuration:
   - Set `api.max-queue-connections` to equal `TotalPooledConnections` (145)
   - This would make `ReservedForNonQueue = 0`
   - Queue would no longer be artificially throttled

### Configuration Workaround

Until the fix is deployed:

**Option A**: Remove the queue connection limit
```
api.max-queue-connections = 145 (same as TotalPooledConnections)
```
This makes queue reservation = 0, allowing queue to use all connections.

**Option B**: Reduce the reservation
```
api.max-queue-connections = 135
```
This makes reservation = 10 instead of 115, giving queue much more breathing room.

**Option C**: Increase total connections
```
TotalPooledConnections = 200
api.max-queue-connections = 30
```
This makes reservation = 170, but with 200 total, queue can work with 30 free.

## Validation That Queue IS Working (Despite Throttling)

Evidence from logs:
- ✅ Queue IS processing: "Processing 'Axone.2019.1080p.NF.WEB-DL.DDP5.1.x264-Telly'"
- ✅ Connections ARE being allocated: "Connection reused for Queue"
- ✅ Progress is happening: "Task completed 54/81"
- ⚠️ But using only 5 connections instead of 30: "Usage=Queue=5"
- ⚠️ 59 waiters in the semaphore: "Waiters=59"

The queue IS working, but it's severely throttled and slow due to the misconfigured reservation.

## Conclusion

**Status**: Queue is working but severely throttled

**Root Cause**: Reservation calculation (`ReservedForNonQueue=115`) leaves only 1 connection buffer (116 needed, 115 available), causing near-deadlock conditions.

**Impact**: Queue uses only ~5 connections instead of intended 30, processing is very slow.

**Solution**: Need to rebuild Docker image with the fixes I implemented, OR adjust configuration to reduce/eliminate reservation.
