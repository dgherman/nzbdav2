# Global Operation Limiter - Implementation Complete

## Summary

Successfully implemented **Option 3: Global Operation Coordinator** from the QoS architecture analysis. The operation limits (Queue=30, HealthCheck=60, Streaming=55) now apply **globally across ALL providers** instead of per-provider.

## Problem Solved

### Before (Incorrect):
```
Provider 1 (95 connections)
    └── OperationLimitedPool (Queue=30, HealthCheck=60, Streaming=55)
Provider 2 (50 connections)
    └── OperationLimitedPool (Queue=30, HealthCheck=60, Streaming=55)

Result: 60+60=120 HealthCheck operations possible (double the limit!)
```

### After (Correct):
```
GlobalOperationLimiter (Queue=30, HealthCheck=60, Streaming=55)
    ↓
    [Acquire global permit BEFORE requesting connection]
    ↓
MultiProviderNntpClient (routes to best provider)
    ├── Provider 1: ConnectionPool(95) ──┐
    └── Provider 2: ConnectionPool(50) ──┴──> Global Semaphore (145 total)
```

## Changes Made

### 1. New File: `GlobalOperationLimiter.cs`

Created a lightweight coordinator that enforces global limits:

**Location:** `/home/ubuntu/nzbdav/backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs`

**Key Features:**
- Uses separate semaphores for each operation type (Queue, HealthCheck, Streaming)
- Permits must be acquired BEFORE requesting provider connections
- Implements `IDisposable` with `OperationPermit` struct for automatic cleanup
- QoS ballooning: Operations can exceed guarantee when others are idle
- Logs usage breakdown for monitoring

**API:**
```csharp
// Acquire permit (blocks if limit reached)
var permit = await globalLimiter.AcquirePermitAsync(usageType, cancellationToken);

// Use permit (automatically released when disposed)
using (permit)
{
    // Request connection from provider...
}
```

### 2. Modified: `MultiConnectionNntpClient.cs`

Updated to acquire global permits before using connections:

**Changes:**
- Added `GlobalOperationLimiter?` parameter to constructor (optional for backward compatibility)
- Modified `RunWithConnection<T>` to acquire permit before requesting connection
- Added proper cleanup in retry logic (release old permit, acquire new one)
- Added `using NzbWebDAV.Extensions` for `GetContext<T>` extension method

**Flow:**
```
1. RunWithConnection called
2. IF global limiter configured:
   - Get operation type from CancellationToken context
   - Acquire global permit (may block if limit reached)
3. Request connection from ConnectionPool
4. Execute operation
5. Release permit (via using/Dispose)
```

### 3. Modified: `UsenetStreamingClient.cs`

Updated to create ONE shared global limiter for all providers:

**Changes:**
- Modified `CreateNewConnectionPool` - Removed OperationLimitedPool wrapping, returns plain `ConnectionPool<T>`
- Modified `CreateMultiProviderClient` - Creates single `GlobalOperationLimiter` instance
- Modified `CreateProviderClient` - Accepts `GlobalOperationLimiter` and passes to `MultiConnectionNntpClient`
- Removed all references to per-provider operation limits

**Architecture:**
```csharp
// ONE global limiter created
var globalLimiter = new GlobalOperationLimiter(
    maxQueueConnections,        // 30
    maxHealthCheckConnections,  // 60
    totalPooledConnectionCount  // 145
);

// Passed to ALL providers
var providerClients = providerConfig.Providers
    .Select((provider, index) => CreateProviderClient(
        provider,
        // ...
        globalLimiter  // ← Same instance for ALL
    ))
    .ToList();
```

### 4. Removed: `OperationLimitedConnectionPool.cs`

Deleted the per-provider pool wrapper - no longer needed.

### 5. Updated: `Program.cs`

Changed build version identifier:
- Old: `BUILD v2025-11-29-QOS-LIMITS`
- New: `BUILD v2025-11-29-GLOBAL-LIMITS`

## Expected Behavior

### Operation Limits (Global):
```
Total: 145 connections (Provider1: 95 + Provider2: 50)
├── Queue: 30 (guaranteed globally)
├── HealthCheck: 60 (guaranteed/max globally)
└── Streaming: 55 (guaranteed globally)
```

### QoS Ballooning:
- **Idle system:** Queue can use up to 145 connections (balloons into unused capacity)
- **HealthCheck active:** Queue limited to 30, HealthCheck gets up to 60, Streaming can balloon
- **All active:** Each gets its guarantee (30+60+55=145)

### Multi-Provider Routing:
1. Operation requests connection
2. GlobalOperationLimiter checks: Can this operation type proceed?
   - Yes: Acquire permit (semaphore slot)
   - No: Block until another operation releases
3. MultiProviderNntpClient picks best provider (most idle connections)
4. Provider's ConnectionPool acquires from global semaphore (145 total)
5. Operation executes
6. Connection returned to pool
7. Permit released

## Verification

### Log Monitoring:

**Startup:**
```
[GlobalOperationLimiter] Initialized: Queue=30, HealthCheck=60, Streaming=55, Total=145
```

**During operation:**
```
[GlobalOperationLimiter] Acquired permit for Queue: Current=5, Guaranteed=30, Usage=Queue=5,HealthCheck=42,BufferedStreaming=30
[GlobalOperationLimiter] Released permit for HealthCheck: Usage=Queue=5,HealthCheck=41,BufferedStreaming=30
```

### Expected Results:
- ❌ **Before:** HealthCheck could reach 120 (60 per provider × 2)
- ✅ **After:** HealthCheck hard-capped at 60 (global limit)

- ❌ **Before:** Queue starved at 11-25 despite 30 guarantee (per-provider limits allowed starvation)
- ✅ **After:** Queue guaranteed 30 even when HealthCheck/Streaming active

- ❌ **Before:** Total tracked operations could reach 290 (145 per provider × 2)
- ✅ **After:** Total operations capped at 145 (global limit = physical connection limit)

## Build Information

- **Docker Image:** `local/nzbdav:3`
- **Build Version:** `v2025-11-29-GLOBAL-LIMITS`
- **Build Status:** ✅ Successfully compiled
- **Warnings:** Only standard nullable reference warnings (expected, not errors)

## Deployment

To deploy:
```bash
docker stop nzbdav
docker rm nzbdav
docker run -d --name nzbdav \
  -p 3000:3000 \
  -v $(pwd)/config:/config \
  local/nzbdav:3
```

Then monitor logs:
```bash
docker logs -f nzbdav | grep -E "(GlobalOperationLimiter|Usage=)"
```

## Testing Checklist

1. **Verify global limits enforced:**
   - HealthCheck should never exceed 60 (not 120)
   - Queue should get 30 even when HealthCheck at 60
   - Total usage should not exceed 145

2. **Verify QoS ballooning:**
   - With no HealthCheck, Queue should be able to use 100+ connections
   - When HealthCheck starts, Queue should naturally drop to ~30

3. **Verify multi-provider routing:**
   - Both providers should be utilized
   - Operations should prefer provider with most idle connections

4. **Verify Queue processing:**
   - Queue should now use closer to 30 connections (not stuck at 11-25)
   - Queue items should process faster with proper connection allocation

## Architecture Benefits

1. **Simple & Correct:** One global limiter = one source of truth
2. **Fair Allocation:** Guarantees enforced globally, not per-provider
3. **Efficient:** QoS ballooning uses excess capacity when available
4. **Transparent:** Logs show actual global usage breakdown
5. **Provider-Agnostic:** Limits apply consistently regardless of provider configuration

## Next Steps

1. Deploy `local/nzbdav:3` to production
2. Monitor `[GlobalOperationLimiter]` logs for usage patterns
3. Verify Queue processing speed improves
4. Confirm HealthCheck stays at/below 60
5. Check total usage stays at/below 145

## Comparison: Before vs. After

| Metric | Before (Per-Provider Limits) | After (Global Limits) |
|--------|------------------------------|----------------------|
| HealthCheck Max | 120 (60×2 providers) | 60 (global) ✅ |
| Queue Guarantee | 30 per provider (unreliable) | 30 global (enforced) ✅ |
| Total Operations | Up to 290 (145×2) | 145 (matches physical) ✅ |
| Provider Fairness | Imbalanced (provider affinity) | Balanced (best provider picked) ✅ |
| Complexity | High (2 OperationLimitedPools) | Low (1 GlobalOperationLimiter) ✅ |

