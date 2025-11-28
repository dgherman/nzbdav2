# Connection Allocation Fix - Queue Starvation Issue

## Problem Summary

Queue items were not being allocated connections even though 30 connections were pre-allocated for queue processing. Health checks and buffered streaming operations were consuming all available connections, starving the queue processor.

## Root Cause Analysis

### The Architecture

The connection pool uses a priority-based reservation system via `ReservedPooledConnectionsContext`:

1. **Queue Processing** sets `reservedConnections = TotalPooledConnections - MaxQueueConnections`
   - Example: 50 total - 30 for queue = 20 reserved for non-queue operations
   - Queue requests connections with `requiredAvailable = 20`
   - This means: "Only acquire a connection if **more than 20** connections remain free"

2. **Streaming/Health Check Operations** (before this fix) didn't set any reserved context
   - Defaulted to `requiredAvailable = 0`
   - Could acquire connections as long as **any** connection was free
   - Had higher priority access to connections

### The Deadlock Scenario

With configuration:
- Total connections: 50
- Max queue connections: 30 (reserves 20 for non-queue)

**What happened:**
1. Health check or streaming starts, uses 31+ connections (requiredAvailable=0)
2. Queue tries to acquire connection with requiredAvailable=20
3. Current available connections: 50 - 31 = 19 (not enough!)
4. Queue waits for `CurrentCount > 20` (needs 21+ free)
5. Health check/streaming operations hold connections
6. **Deadlock**: Queue waits forever, connections never freed

### Why This Happened

The `ExtendedSemaphoreSlim` logic (backend/Clients/Usenet/Connections/ExtendedSemaphoreSlim.cs:112):

```csharp
if (observed <= reqAvail) return false; // need at least reqAvail+1 to take one
```

This means:
- With `requiredAvailable = 0`: Acquire if `CurrentCount >= 1`
- With `requiredAvailable = 20`: Acquire if `CurrentCount >= 21`

**Priority inversion**: Operations that should have **lower priority** (streaming) were getting connections **before** the queue because they had a lower `requiredAvailable` threshold.

## The Fix

### Solution Overview

Set `ReservedPooledConnectionsContext` for **ALL** HTTP operations (WebDAV streaming, etc.) to respect the queue's reservation.

### Changes Made

#### 1. Added ConfigManager Method (backend/Config/ConfigManager.cs)

```csharp
/// <summary>
/// Gets the number of connections that should be reserved for queue processing.
/// All non-queue operations (streaming, health checks) should set this as their
/// ReservedPooledConnectionsContext to ensure queue processing gets priority.
/// </summary>
public int GetReservedConnectionsForQueue()
{
    var providerConfig = GetUsenetProviderConfig();
    var maxQueueConnections = GetMaxQueueConnections();
    return Math.Max(0, providerConfig.TotalPooledConnections - maxQueueConnections);
}
```

#### 2. Created ReservedConnectionsMiddleware (backend/Middlewares/ReservedConnectionsMiddleware.cs)

Middleware that sets `ReservedPooledConnectionsContext` on **every HTTP request**:

```csharp
public async Task InvokeAsync(HttpContext context)
{
    var reservedForQueue = _configManager.GetReservedConnectionsForQueue();
    var reservedContext = new ReservedPooledConnectionsContext(reservedForQueue);

    using var contextScope = context.RequestAborted.SetScopedContext(reservedContext);

    await _next(context);
}
```

This ensures:
- WebDAV streaming operations inherit the reserved context
- All HTTP-based operations respect queue's priority
- Context is automatically cleaned up when request completes

#### 3. Updated NzbFileStream (backend/Streams/NzbFileStream.cs)

Fixed context propagation when creating child cancellation tokens:

```csharp
// Copy ReservedPooledConnectionsContext from parent token
var reservedContext = ct.GetContext<ReservedPooledConnectionsContext>();
var reservedScope = _streamCts.Token.SetScopedContext(reservedContext);

// Keep scope alive for stream's lifetime
_contextScope = new CompositeDisposable(reservedScope, usageScope);
```

**Why this matters**: `NzbFileStream` creates a child cancellation token that lives for the stream's lifetime. Without copying the reserved context, the child token would default to `requiredAvailable = 0`, bypassing the queue's reservation.

#### 4. Created CompositeDisposable (backend/Utils/CompositeDisposable.cs)

Helper class to manage multiple context scopes that need to stay alive together:

```csharp
public sealed class CompositeDisposable : IDisposable
{
    private readonly IDisposable[] _disposables;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable?.Dispose();
    }
}
```

#### 5. Registered Middleware (backend/Program.cs)

Added middleware early in the pipeline:

```csharp
app.UseMiddleware<ExceptionMiddleware>();
app.UseMiddleware<ReservedConnectionsMiddleware>();  // NEW
app.UseWebSockets();
```

## How It Works Now

### With Configuration:
- Total connections: 50
- Max queue connections: 30
- Reserved for queue: 20

### Connection Allocation Priority:

1. **Queue Processing**
   - `requiredAvailable = 20` (reserves 20 for others, can use 30)
   - **High priority**: Gets connections as long as 21+ are free
   - Can use up to 30 connections simultaneously

2. **Streaming/Health Checks** (after fix)
   - `requiredAvailable = 20` (same as queue!)
   - **Equal priority**: Also waits for 21+ free connections
   - Prevented from monopolizing all connections

### Fair Sharing:

With both operations using `requiredAvailable = 20`:
- **Both** queue and streaming can acquire connections when 21+ are free
- **Neither** can monopolize all connections
- **First-come-first-served** within the same priority level
- Queue processing no longer starves

## Testing Recommendations

1. **Verify queue processing starts**:
   - Add NZB to queue
   - Check logs for: `[Queue] Processing 'JobName': TotalConnections=50, MaxQueueConnections=30, ReservedForNonQueue=20`
   - Verify queue progress updates

2. **Test with concurrent streaming**:
   - Start streaming a video file via WebDAV
   - Add NZB to queue
   - Both should make progress without starvation

3. **Monitor connection pool stats**:
   - Check logs for: `[ConnPool] Requesting connection for Queue: Live=X, Idle=Y, Active=Z, Available=W`
   - Verify connections are being allocated to queue items

4. **Verify no deadlocks**:
   - Run health checks + streaming + queue processing simultaneously
   - All operations should complete without hanging

## Configuration Notes

### When `api.max-queue-connections` is NOT set:
- Defaults to `TotalPooledConnections`
- `GetReservedConnectionsForQueue()` returns 0
- All operations can use all connections
- No reservation, but no priority issues either

### When `api.max-queue-connections` IS set (e.g., 30):
- `GetReservedConnectionsForQueue()` returns 20 (if total is 50)
- All operations must leave 20 connections available
- Queue gets fair access to its allocated 30 connections
- Prevents streaming from monopolizing the pool

## Benefits

1. **Eliminates queue starvation**: Queue items always get connections when available
2. **Fair resource sharing**: All operations have equal priority
3. **Configurable priorities**: Via `api.max-queue-connections` setting
4. **No deadlocks**: Consistent reservation across all operations
5. **Automatic context propagation**: Middleware handles it for all HTTP requests

## Related Files

- `backend/Config/ConfigManager.cs` - Configuration methods
- `backend/Middlewares/ReservedConnectionsMiddleware.cs` - HTTP middleware
- `backend/Streams/NzbFileStream.cs` - Context propagation for streams
- `backend/Utils/CompositeDisposable.cs` - Multiple context management
- `backend/Clients/Usenet/Connections/ExtendedSemaphoreSlim.cs` - Priority semaphore logic
- `backend/Clients/Usenet/Connections/ConnectionPool.cs` - Connection acquisition
- `backend/Queue/QueueItemProcessor.cs` - Queue processing with reservation

## Future Improvements

1. **Dynamic priority adjustment**: Could adjust reservation based on queue depth
2. **Connection usage tracking**: Already implemented, could use for auto-tuning
3. **Health check throttling**: Further reduce health check concurrency when queue is active
4. **Metrics/monitoring**: Expose connection allocation metrics via API
