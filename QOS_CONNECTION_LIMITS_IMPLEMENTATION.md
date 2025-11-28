# QoS-Style Connection Limits Implementation

## Summary

Replaced the complex `ReservedPooledConnectionsContext` + `ExtendedSemaphoreSlim` reservation system with a simple **QoS-style operation-limited connection pool** that enforces hard limits per operation type with ballooning support.

## Changes Made

### New File: `OperationLimitedConnectionPool.cs`
- Wraps existing `ConnectionPool<T>` to add per-operation limits
- Uses separate semaphores for each operation type (Queue, HealthCheck, Streaming)
- **Ballooning**: Operations can use more than their guaranteed allocation when capacity is available
- **Priority enforcement**: When other operations need connections, operations yield back to their guaranteed limit
- Implements `IDisposable` to properly clean up resources

### Modified Files

#### `UsenetStreamingClient.cs`
- Added `_configManager` field to access operation limits
- Modified `CreateNewConnectionPool()` to wrap inner pool with `OperationLimitedConnectionPool`
- Modified `CreateMultiProviderClient()` to pass operation limits from config
- Modified `CreateProviderClient()` to accept and forward operation limit parameters
- Removed `ReservedPooledConnectionsContext` copying in `CheckAllSegmentsAsync()`

#### `MultiConnectionNntpClient.cs`
- Changed constructor parameter from `ConnectionPool<T>` to `OperationLimitedConnectionPool<T>`
- Updated `UpdateConnectionPool()` method signature to accept `OperationLimitedConnectionPool<T>`
- Updated private field `_connectionPool` to use new type

#### `HealthCheckService.cs`
- Removed `ReservedPooledConnectionsContext` setting
- Operation limits now enforce the 60 connection maximum automatically

#### `NzbFileStream.cs`
- Removed `ReservedPooledConnectionsContext` copying
- Simplified context management - only `ConnectionUsageContext` needed now

#### `Program.cs`
- Removed `ReservedConnectionsMiddleware` from pipeline
- Updated build version to `v2025-11-29-QOS-LIMITS`

### Removed Complexity
- No more `ReservedConnectionsMiddleware`
- No more `ReservedPooledConnectionsContext` propagation
- No more confusing "RequiredAvailable" semantics
- No more inverted logic calculations

## How It Works

### Configuration
```
Total Pool: 145 connections
├── Queue: 30 connections (guaranteed)
├── HealthCheck: 60 connections (guaranteed)
└── Streaming: 55 connections (remaining, guaranteed)
```

### QoS Ballooning Behavior

**Scenario 1: Only Queue is active**
- Queue can use up to ALL 145 connections (balloons beyond its 30 guarantee)
- No other operations are running, so excess capacity is available

**Scenario 2: Queue + HealthCheck active**
- HealthCheck starts and needs connections
- Queue operations that try to acquire beyond their 30 guarantee will block
- Existing Queue connections beyond 30 complete naturally and aren't force-released
- Once Queue drops to ≤30, HealthCheck can acquire up to its 60 limit
- HealthCheck can balloon beyond 60 if Queue isn't using all its allocation

**Scenario 3: Queue + HealthCheck + Streaming active**
- Each operation can use up to its guaranteed limit
- If one operation is idle, others can balloon into that space
- When the idle operation becomes active, balloon connections naturally drain back

### Implementation Details

**Per-Operation Semaphores:**
- `_queueSemaphore`: SemaphoreSlim(30, 30)
- `_healthCheckSemaphore`: SemaphoreSlim(60, 60)
- `_streamingSemaphore`: SemaphoreSlim(55, 55)

**Connection Acquisition:**
1. Operation requests connection
2. Wait for operation-specific semaphore (enforces guaranteed limit)
3. Request from inner pool (may use ballooned capacity if available)
4. Track usage by operation type
5. On release: Release semaphore + decrement usage counter

**Ballooning Magic:**
- The inner pool has 145 total connections
- Operation semaphores enforce minimums (30+60+55=145)
- When an operation's semaphore has free slots, it can acquire from inner pool
- The inner pool may have MORE than the operation's guarantee available (ballooning)
- Other operations see increased contention and naturally back off

## Expected Behavior

### Queue Processing
- Guaranteed 30 connections minimum
- Can balloon up to 145 when idle system
- Always gets priority via guaranteed allocation

### HealthCheck
- **Hard limit: 60 connections maximum** (enforced by semaphore)
- Can balloon down to 30 if Queue is heavily active
- Will never exceed 60 regardless of system capacity

### Streaming
- Guaranteed 55 connections
- Can balloon to use unused Queue/HealthCheck capacity
- Per-stream limit of 5 connections still applies (configured separately)

## Benefits

1. **Clear, enforceable limits** - Each operation has a hard maximum
2. **No starvation** - Guaranteed minimums ensure fair allocation
3. **Efficient resource usage** - Ballooning uses excess capacity
4. **Simple semantics** - No confusing "reserve X for Y" logic
5. **Easy to verify** - Usage logs show actual counts per operation type

## Testing Verification

Monitor logs for:
```
[OperationLimitedPool] Initialized: Queue=30, HealthCheck=60, Streaming=55, Total=145
[OperationLimitedPool] Requesting for Queue: Current=X, Guaranteed=30, Usage=Queue=X,HealthCheck=Y
[OperationLimitedPool] Released Queue: Usage=Queue=X,HealthCheck=Y,Streaming=Z
```

Expected behavior:
- HealthCheck never exceeds 60
- Queue gets up to 30 even when HealthCheck is active
- Total usage can balloon up to 145
- No more 138+ waiters piling up at semaphore boundary

## Build Information

Successfully built as Docker image `local/nzbdav:2` with build version `v2025-11-29-QOS-LIMITS`.
