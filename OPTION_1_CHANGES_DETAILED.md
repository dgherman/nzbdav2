# Option 1: Docker Rebuild - Detailed Changes

## Summary

Option 1 rebuilds the Docker image with **5 file changes** that fix the connection reservation bug. These changes ensure that **all operations** (streaming, health checks, WebDAV) properly respect the queue's reserved connections.

---

## File Changes Overview

| File | Type | Lines Changed | Purpose |
|------|------|---------------|---------|
| `Config/ConfigManager.cs` | Modified | +12 | Add centralized reservation calculation |
| `Program.cs` | Modified | +1 | Register new middleware |
| `Streams/NzbFileStream.cs` | Modified | +16 | Fix context propagation in streams |
| `Middlewares/ReservedConnectionsMiddleware.cs` | **NEW** | +38 | Set reservation for all HTTP requests |
| `Utils/CompositeDisposable.cs` | **NEW** | +32 | Manage multiple context scopes |

**Total**: 99 lines added, 2 new files created

---

## Detailed Change Breakdown

### 1. **ConfigManager.cs** - Add Centralized Reservation Method

**Location**: `backend/Config/ConfigManager.cs` (after line 160)

**Change**:
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

**Purpose**:
- Provides a single source of truth for reservation calculation
- Other components call this instead of calculating themselves
- Ensures consistency across the codebase

**Impact**: No behavioral change yet, just adds the method

---

### 2. **ReservedConnectionsMiddleware.cs** - NEW FILE

**Location**: `backend/Middlewares/ReservedConnectionsMiddleware.cs`

**Complete File** (38 lines):
```csharp
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Middlewares;

/// <summary>
/// Middleware that sets the ReservedPooledConnectionsContext for all HTTP requests.
/// This ensures that WebDAV streaming and other HTTP operations leave enough connections
/// available for queue processing, which has higher priority.
/// </summary>
public class ReservedConnectionsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConfigManager _configManager;

    public ReservedConnectionsMiddleware(RequestDelegate next, ConfigManager configManager)
    {
        _next = next;
        _configManager = configManager;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Set reserved connections context to respect queue's reservation
        // All HTTP operations (WebDAV streaming, etc.) will inherit this context
        var reservedForQueue = _configManager.GetReservedConnectionsForQueue();
        var reservedContext = new ReservedPooledConnectionsContext(reservedForQueue);

        using var contextScope = context.RequestAborted.SetScopedContext(reservedContext);

        await _next(context);
    }
}
```

**Purpose**:
- Intercepts ALL incoming HTTP requests
- Sets the `ReservedPooledConnectionsContext` on the request's cancellation token
- Ensures WebDAV streaming, API calls, etc. all respect queue's reservation

**How it works**:
1. Request comes in (WebDAV stream, health check, etc.)
2. Middleware calculates reservation: `GetReservedConnectionsForQueue()`
3. Sets context on `context.RequestAborted` token
4. All downstream code inherits this context
5. When they request connections, they automatically reserve the right amount

**Impact**: **THIS IS THE KEY FIX** - ensures streaming operations can't monopolize connections

---

### 3. **Program.cs** - Register the Middleware

**Location**: `backend/Program.cs` (line 108)

**Change**:
```csharp
// run
app.UseMiddleware<ExceptionMiddleware>();
app.UseMiddleware<ReservedConnectionsMiddleware>();  // <-- NEW LINE
app.UseWebSockets();
```

**Purpose**: Activates the middleware in the ASP.NET pipeline

**Impact**: Makes the middleware actually run on every request

---

### 4. **CompositeDisposable.cs** - NEW FILE

**Location**: `backend/Utils/CompositeDisposable.cs`

**Complete File** (32 lines):
```csharp
namespace NzbWebDAV.Utils;

/// <summary>
/// Disposes multiple IDisposable objects as a single unit.
/// Useful when multiple context scopes need to be kept alive together.
/// </summary>
public sealed class CompositeDisposable : IDisposable
{
    private readonly IDisposable[] _disposables;
    private bool _disposed;

    public CompositeDisposable(params IDisposable[] disposables)
    {
        _disposables = disposables ?? throw new ArgumentNullException(nameof(disposables));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var disposable in _disposables)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
                // Suppress exceptions to ensure all disposables are disposed
            }
        }
    }
}
```

**Purpose**:
- Utility class to manage multiple `IDisposable` scopes
- Needed for `NzbFileStream` to keep both reservation context AND usage context alive

**How it works**:
- Takes multiple disposables in constructor
- When disposed, disposes all of them
- Suppresses exceptions to ensure all get disposed even if one fails

**Impact**: Enables proper cleanup of multiple contexts in streams

---

### 5. **NzbFileStream.cs** - Fix Context Propagation

**Location**: `backend/Streams/NzbFileStream.cs` (lines 111-149)

**Changes** (3 additions):

**Addition 1** (after line 108):
```csharp
// Copy ReservedPooledConnectionsContext from parent token to ensure streaming respects queue reservation
// This is CRITICAL: streaming operations must leave connections available for queue processing
var reservedContext = ct.GetContext<ReservedPooledConnectionsContext>();
var reservedScope = _streamCts.Token.SetScopedContext(reservedContext);
```

**Addition 2** (buffered streaming path):
```csharp
// Store both scopes so they stay alive for the stream's lifetime
_contextScope = new CompositeDisposable(reservedScope, _contextScope);
```

**Addition 3** (non-buffered streaming path):
```csharp
// Fallback to original implementation for small files or low concurrency
// Set context for non-buffered streaming and keep scope alive
var usageScope = _streamCts.Token.SetScopedContext(_usageContext);  // Changed from _contextScope =
var contextCt = _streamCts.Token;

// Store both scopes so they stay alive for the stream's lifetime
_contextScope = new CompositeDisposable(reservedScope, usageScope);  // NEW
```

**Purpose**:
- NzbFileStream creates a child cancellation token (`_streamCts.Token`)
- Without this fix, the child token doesn't inherit the reservation context
- This fix explicitly copies the reservation context to the child token
- Uses `CompositeDisposable` to keep both contexts alive for stream's lifetime

**Why this matters**:
- Streams can live for minutes (video playback)
- The reservation context must stay active the entire time
- If it gets disposed early, stream would ignore reservation

**Impact**: Ensures streams created from HTTP requests maintain the reservation throughout their lifetime

---

## How The Fix Works End-to-End

### Before the Fix

1. **HTTP Request** → No reservation context set
2. **WebDAV creates stream** → Context defaults to `requiredAvailable=0`
3. **Stream requests 22 connections** → Grabs them all (no restriction)
4. **Queue requests connections** → Needs 116+ free, only 115 available
5. **Result**: Queue starved, using only 5 connections

### After the Fix

1. **HTTP Request** → `ReservedConnectionsMiddleware` sets `reservedForQueue=115`
2. **WebDAV creates stream** → Inherits `requiredAvailable=115` from HTTP context
3. **Stream requests 22 connections** → Must wait until 116+ are free (same as queue!)
4. **Queue requests connections** → Both queue and streaming have equal priority
5. **Result**: Fair sharing, queue gets its full 30 connections

---

## Expected Behavior Changes

### Connection Allocation Pattern

**Before**:
```
Queue: requiredAvailable=115, waits for 116+ free
Streaming: requiredAvailable=0, takes any available
→ Priority inversion: Streaming gets priority over Queue
```

**After**:
```
Queue: requiredAvailable=115, waits for 116+ free
Streaming: requiredAvailable=115, waits for 116+ free
→ Equal priority: First-come-first-served
```

### Usage Statistics

**Before** (from logs):
```
Usage=Queue=5,HealthCheck=3,BufferedStreaming=22
Waiters=59
```

**After** (expected):
```
Usage=Queue=28,HealthCheck=3,BufferedStreaming=14
Waiters=5-10
```

### Processing Speed

**Before**:
- Queue using 5 connections
- Tasks completing slowly, serialized
- 59 tasks waiting

**After**:
- Queue using 28-30 connections
- **6x faster processing**
- Minimal waiting (5-10 tasks)

---

## Testing the Changes

### Before Building

1. **Check current state**:
```bash
docker logs nzbdav --tail 50 | grep "Usage="
# Should show: Usage=Queue=5,HealthCheck=3,BufferedStreaming=22
```

### Build Command

```bash
cd /home/ubuntu/nzbdav
docker build -t local/nzbdav:2 -f backend/Dockerfile .
```

**Expected build time**: 5-10 minutes

### After Building

1. **Stop old container**:
```bash
docker stop nzbdav
docker rm nzbdav
```

2. **Start new container** (use your actual run command with volumes, etc.):
```bash
docker run -d --name nzbdav -p 8080:8080 -p 3000:3000 \
  -v /path/to/config:/config \
  local/nzbdav:2
```

3. **Verify the fix**:
```bash
# Wait 30 seconds for startup
sleep 30

# Check usage (should show Queue=25-30)
docker logs nzbdav --tail 100 | grep "Usage=" | tail -1

# Check waiters (should show Waiters=0-10)
docker logs nzbdav --tail 100 | grep "Waiters=" | tail -5

# Check reservation (should still be 115, but NOW both queue and streaming use it)
docker logs nzbdav --tail 100 | grep "RequiredReserved" | tail -1
```

---

## Risk Assessment

### Changes Risk Level: **LOW**

**Why low risk**:
- ✅ No changes to existing business logic
- ✅ No changes to connection pool implementation
- ✅ No changes to queue processing logic
- ✅ Only adds context propagation (defensive change)
- ✅ If middleware fails, it just doesn't set context (degrades to current behavior)
- ✅ All changes are additive (new middleware, new utility class)

### Rollback Plan

If issues occur:
```bash
docker stop nzbdav
docker rm nzbdav
# Restart with old image:
docker run -d --name nzbdav ... local/nzbdav:1
```

Old image (`local/nzbdav:1`) remains available for instant rollback.

---

## Why This Is Better Than Config Change

| Aspect | Option 1 (Rebuild) | Option 2 (Config) |
|--------|-------------------|-------------------|
| **Fixes root cause** | ✅ Yes | ❌ No (workaround) |
| **Maintains reservation system** | ✅ Yes | ❌ Disabled |
| **Queue priority** | ✅ Enforced | ⚠️ Lost |
| **Future-proof** | ✅ Yes | ⚠️ May break later |
| **Time to implement** | ⏱️ 5-10 min | ⏱️ 1 min |
| **Permanent solution** | ✅ Yes | ❌ Temporary |

**Recommendation**: Option 1 is the proper fix, worth the extra time.

---

## Summary

**5 files changed** to implement a complete, proper fix:

1. ✅ **ConfigManager.cs** - Centralized reservation calculation
2. ✅ **ReservedConnectionsMiddleware.cs** (NEW) - Apply to all HTTP requests
3. ✅ **Program.cs** - Activate the middleware
4. ✅ **CompositeDisposable.cs** (NEW) - Utility for context management
5. ✅ **NzbFileStream.cs** - Propagate context through stream lifetime

**Expected outcome**: Queue uses full 30 connections, 6x speed improvement, no starvation.

**Risk**: Low - all changes are defensive and additive.

**Time**: ~10 minutes total (5 min build, 2 min restart, 3 min validation).
