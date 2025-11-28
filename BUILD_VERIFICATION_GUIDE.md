# Build Verification Guide

## Purpose

This guide shows you how to verify that the correct build (with the connection reservation fixes) is running in your Docker container.

---

## Log Messages to Look For

When the correct build is running, you will see these **distinctive log messages** on container startup:

### 1. Backend Startup Message (appears first)

```
═══════════════════════════════════════════════════════════════
  NzbDav Backend Starting - BUILD v2025-11-28-FIX
  Connection Reservation Fix Applied
═══════════════════════════════════════════════════════════════
```

**Location**: Appears very early in the logs, right after logger initialization

---

### 2. Middleware Activation Message (appears during first HTTP request)

```
╔═══════════════════════════════════════════════════════════════╗
║  ReservedConnectionsMiddleware ACTIVE - Build v2025-11-28    ║
║  Connection reservation fix is ENABLED                        ║
╚═══════════════════════════════════════════════════════════════╝
```

**Location**: Appears when the first HTTP request is processed (usually within 1-2 seconds of startup)

---

### 3. Per-Request Logging (first 5 requests only)

```
[ReservedConnectionsMiddleware] Request #1: Setting ReservedForQueue=115 for GET /health
[ReservedConnectionsMiddleware] Request #2: Setting ReservedForQueue=115 for GET /api/...
[ReservedConnectionsMiddleware] Request #3: Setting ReservedForQueue=115 for PROPFIND /
...
```

**Location**: Appears for the first 5 HTTP requests

**What to verify**:
- `ReservedForQueue` value should match your expected configuration
- Shows the middleware is actually processing requests

---

## How to Check

### After Building and Starting Container

```bash
# Method 1: Check startup logs
docker logs nzbdav 2>&1 | head -50 | grep -A 2 "BUILD v2025-11-28-FIX"

# Should output:
# ═══════════════════════════════════════════════════════════════
#   NzbDav Backend Starting - BUILD v2025-11-28-FIX
#   Connection Reservation Fix Applied
# ═══════════════════════════════════════════════════════════════
```

```bash
# Method 2: Check middleware activation
docker logs nzbdav 2>&1 | grep "ReservedConnectionsMiddleware ACTIVE"

# Should output:
# ║  ReservedConnectionsMiddleware ACTIVE - Build v2025-11-28    ║
```

```bash
# Method 3: Check request processing
docker logs nzbdav 2>&1 | grep "ReservedConnectionsMiddleware] Request"

# Should output several lines like:
# [ReservedConnectionsMiddleware] Request #1: Setting ReservedForQueue=115 for GET /health
# [ReservedConnectionsMiddleware] Request #2: Setting ReservedForQueue=115 for GET /api/...
```

---

## If You DON'T See These Messages

**Problem**: You're running the OLD build without the fixes

**Solutions**:

### Option 1: Verify Build Process

```bash
# Check git status to confirm changes are present
cd /home/ubuntu/nzbdav/backend
git status

# Should show:
# modified:   Config/ConfigManager.cs
# modified:   Program.cs
# modified:   Streams/NzbFileStream.cs
# Untracked files:
#   Middlewares/ReservedConnectionsMiddleware.cs
#   Utils/CompositeDisposable.cs
```

### Option 2: Check Docker Build Context

```bash
# Verify the new files exist
ls -la Middlewares/ReservedConnectionsMiddleware.cs
ls -la Utils/CompositeDisposable.cs

# Verify Program.cs has the log message
grep "BUILD v2025-11-28-FIX" Program.cs

# Should output:
# Log.Warning("  NzbDav Backend Starting - BUILD v2025-11-28-FIX");
```

### Option 3: Force Clean Build

```bash
cd /home/ubuntu/nzbdav

# Remove cached layers
docker builder prune -f

# Build with --no-cache flag
docker build --no-cache -t local/nzbdav:verified -f backend/Dockerfile .

# Start container
docker stop nzbdav && docker rm nzbdav
docker run -d --name nzbdav -p 8080:8080 -p 3000:3000 \
  -v /path/to/config:/config \
  local/nzbdav:verified
```

---

## Complete Verification Checklist

After starting the container with the new build:

- [ ] **Backend startup message** appears in logs
  ```bash
  docker logs nzbdav 2>&1 | grep "BUILD v2025-11-28-FIX"
  ```

- [ ] **Middleware activation** message appears
  ```bash
  docker logs nzbdav 2>&1 | grep "ReservedConnectionsMiddleware ACTIVE"
  ```

- [ ] **Request logging** shows middleware is processing
  ```bash
  docker logs nzbdav 2>&1 | grep "\[ReservedConnectionsMiddleware\] Request"
  ```

- [ ] **No deadlock** - check for normal operation
  ```bash
  docker logs nzbdav --tail 50 | grep "Waiters=" | tail -1
  # Should show Waiters=0-10 (not 100+)
  ```

- [ ] **Queue processing** is working
  ```bash
  docker logs nzbdav --tail 50 | grep "Usage=" | tail -1
  # Should show Usage=Queue=20-30,HealthCheck=...
  ```

---

## Expected Log Output on Startup

Complete example of what you should see:

```
[18:10:00 WRN] ═══════════════════════════════════════════════════════════════
[18:10:00 WRN]   NzbDav Backend Starting - BUILD v2025-11-28-FIX
[18:10:00 WRN]   Connection Reservation Fix Applied
[18:10:00 WRN] ═══════════════════════════════════════════════════════════════
[18:10:00 INF] Starting web server...
[18:10:00 INF] Listening on http://0.0.0.0:8080
[18:10:01 WRN] ╔═══════════════════════════════════════════════════════════════╗
[18:10:01 WRN] ║  ReservedConnectionsMiddleware ACTIVE - Build v2025-11-28    ║
[18:10:01 WRN] ║  Connection reservation fix is ENABLED                        ║
[18:10:01 WRN] ╚═══════════════════════════════════════════════════════════════╝
[18:10:01 INF] [ReservedConnectionsMiddleware] Request #1: Setting ReservedForQueue=115 for GET /health
[18:10:01 INF] [ReservedConnectionsMiddleware] Request #2: Setting ReservedForQueue=115 for GET /api/queue
[18:10:02 INF] [ReservedConnectionsMiddleware] Request #3: Setting ReservedForQueue=115 for PROPFIND /
[18:10:02 INF] [ReservedConnectionsMiddleware] Request #4: Setting ReservedForQueue=115 for GET /api/config
[18:10:02 INF] [ReservedConnectionsMiddleware] Request #5: Setting ReservedForQueue=115 for GET /content/
```

---

## Troubleshooting

### Issue: Messages appear but ReservedForQueue=0

**Meaning**: Configuration has `api.max-queue-connections` set to equal `TotalPooledConnections`

**Effect**: Reservation is disabled (this might be intentional as a workaround)

**Action**: If you want reservation enabled, set `api.max-queue-connections` to something less than total (e.g., 30 out of 145)

---

### Issue: Messages appear but still seeing deadlock

**Check**:
```bash
docker logs nzbdav --tail 100 | grep "RequiredReserved"
```

**Look for**:
- If ALL operations show the same `RequiredReserved` value → Fix is working!
- If different operations show different values → Something else is wrong

---

### Issue: Container fails to start

**Check build errors**:
```bash
docker logs nzbdav 2>&1 | grep -i "error\|exception\|failed"
```

**Common issues**:
- Missing using statement (Serilog)
- Syntax error in code
- File permissions in Docker context

**Solution**: Check the build output for specific error messages

---

## Quick Reference Commands

```bash
# One-liner to verify all 3 log messages
docker logs nzbdav 2>&1 | grep -E "BUILD v2025-11-28-FIX|ReservedConnectionsMiddleware ACTIVE|Request #"

# Check container is healthy
docker logs nzbdav --tail 100 | grep -E "Waiters=|Usage="

# Watch logs in real-time for verification
docker logs -f nzbdav | grep --line-buffered -E "BUILD|Reserved|Request #|Waiters=|Usage="
```

---

## Summary

If you see **all three distinctive messages** (backend startup, middleware activation, request logging), you can be 100% confident you're running the correct build with all fixes applied.

The messages use distinctive Unicode box-drawing characters and specific version strings that make them impossible to miss in the logs.
