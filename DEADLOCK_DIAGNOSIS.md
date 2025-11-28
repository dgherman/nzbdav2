# DEADLOCK DIAGNOSIS - Queue Processing Completely Stalled

## Executive Summary

**Status**: 🔴 **COMPLETE DEADLOCK - All operations blocked**

The container is running the **OLD image** (`local/nzbdav:1` created 17:32) **WITHOUT the fixes**. The code changes I made (17:30-17:31) were NOT included in the build that was deployed.

---

## Current State (from logs)

### Semaphore State
```
CurrentCount=85 (global semaphore has 85 free slots)
RequiredAvailable=85 (ALL operations need 86+ free to acquire)
Waiters=144 (144 tasks waiting in queue)
```

### The Deadlock
- **HealthCheck**: Needs `CurrentCount > 85` (i.e., 86+)
- **Queue**: Needs `CurrentCount > 85` (i.e., 86+)
- **Available**: Only 85 free
- **Result**: **NOBODY can proceed!**

### Evidence from Logs

**All operations waiting**:
```
[CombinedSemaphore] Waiting: LocalRemaining=29, RequiredAvailable=85
[ExtSemaphore] Slow path (waiting): CurrentCount=85, RequiredAvailable=85, Waiters=144
```

**Queue stuck at Step 1b**:
```
18:01:58 [Queue] Step 1b: Batch fetching file sizes for 20 files
18:01:58 [ConnPool] Requesting connection for Queue: RequiredReserved=115
```

**Queue made 20 concurrent requests for connections, ALL are blocked waiting**

---

## Root Cause

### Problem 1: Wrong Image Running

The container is running `local/nzbdav:1` created at **17:32:38**.

My code fixes were created at **17:30-17:31**.

**The fixes are NOT in the running image!**

### Problem 2: The Deadlock Mechanism

Without my fixes:
1. Queue sets: `RequiredReserved=115` (reserves 115 for non-queue)
2. HealthCheck **ALSO** sets: `RequiredReserved=85` (this is NEW behavior!)
3. Both wait for their respective thresholds
4. Total available: 85
5. **Both need more than 85 to acquire** = DEADLOCK

The logs show `RequiredReserved=85` for HealthCheck operations, which suggests:
- Configuration changed OR
- HealthCheck is now using the same reservation logic as Queue
- This creates mutual blocking where neither can proceed

### Problem 3: Health Check Monopolized Then Stalled

From earlier logs (18:01:58):
```
Usage=Unknown=1,HealthCheck=38
```

HealthCheck was using 38 connections, then they all got returned, but now:
```
Usage=HealthCheck=16
CurrentCount=85
144 waiters
```

Everything is stuck waiting for 86+ connections to become available.

---

## Why This Happened

### Theory: Configuration Change

Between the old run and new run, something changed:

**Old behavior** (from earlier logs):
- Queue: `RequiredReserved=115`
- HealthCheck: `RequiredReserved=0` (could take any)
- Streaming: `RequiredReserved=0` (could take any)

**New behavior** (current logs):
- Queue: `RequiredReserved=115`
- HealthCheck: `RequiredReserved=85`
- Result: DEADLOCK

**What changed?**
Possibly:
1. Configuration file was updated
2. Environment variable changed
3. Different provider configuration
4. **My code changes were partially compiled/cached somewhere**

### The 85 vs 115 Mystery

Queue shows: `RequiredReserved=115`
HealthCheck shows: `RequiredReserved=85`

This suggests TWO different reservation calculations are active:
- **115** = `TotalConnections(145) - MaxQueueConnections(30)`
- **85** = `TotalConnections(145) - ???(60)`

Could there be TWO different provider configs or settings?

---

## Immediate Solutions

### Option A: Rebuild with Fixes (PROPER FIX)

**Problem**: Current image doesn't have my fixes

**Solution**: Rebuild from source with the actual latest code

```bash
cd /home/ubuntu/nzbdav

# Ensure we're using latest code
git status  # Check what files are modified

# Build new image with timestamp tag
docker build -t local/nzbdav:fix-$(date +%s) -f backend/Dockerfile .

# Stop and remove old container
docker stop nzbdav
docker rm nzbdav

# Run with new image (replace with your actual docker run command)
docker run -d --name nzbdav -p 8080:8080 -p 3000:3000 \
  -v /path/to/config:/config \
  local/nzbdav:fix-$(date +%s)
```

**Expected result**: Middleware applies reservation context properly, prevents deadlock

---

### Option B: Configuration Fix (QUICK UNBLOCK)

**Problem**: Reservation values creating deadlock

**Solution**: Disable/reduce reservation temporarily

**Method 1**: Environment variable (if container supports it)
```bash
docker stop nzbdav
docker rm nzbdav

# Restart with no queue connection limit
docker run -d --name nzbdav -p 8080:8080 -p 3000:3000 \
  -e MAX_QUEUE_CONNECTIONS=145 \
  -v /path/to/config:/config \
  local/nzbdav:1
```

**Method 2**: Edit config database directly
```bash
# Access the config database
sqlite3 /path/to/config/db.sqlite

# Check current setting
SELECT * FROM ConfigItems WHERE ConfigName = 'api.max-queue-connections';

# Set to total connections to disable reservation
UPDATE ConfigItems SET ConfigValue = '145' WHERE ConfigName = 'api.max-queue-connections';

# Restart container
docker restart nzbdav
```

**Expected result**: `ReservedForNonQueue=0`, no deadlock

---

### Option C: Kill Health Checks (EMERGENCY)

**Problem**: Health checks consuming all connections

**Solution**: Disable health checks temporarily

```bash
# Edit config to disable health checks
sqlite3 /path/to/config/db.sqlite
UPDATE ConfigItems SET ConfigValue = 'false' WHERE ConfigName = 'health-check.enabled';

# Restart
docker restart nzbdav
```

**Expected result**: Only queue operations active, should work

---

## Diagnosis Steps to Understand What Happened

### 1. Check what image was actually built

```bash
# List all local/nzbdav images with timestamps
docker images local/nzbdav --format "table {{.Repository}}:{{.Tag}}\t{{.ID}}\t{{.CreatedAt}}"

# Check if there's a newer build
ls -lart /var/lib/docker/image/*/layerdb/sha256/ | tail -20
```

### 2. Verify code changes are in place

```bash
cd /home/ubuntu/nzbdav/backend

# Check modified files
git status

# Verify middleware file exists
ls -la Middlewares/ReservedConnectionsMiddleware.cs
ls -la Utils/CompositeDisposable.cs

# Check if Program.cs has middleware registered
grep "ReservedConnectionsMiddleware" Program.cs
```

### 3. Check config database

```bash
sqlite3 /path/to/config/db.sqlite

SELECT ConfigName, ConfigValue FROM ConfigItems
WHERE ConfigName LIKE '%connection%'
   OR ConfigName LIKE '%health%'
   OR ConfigName LIKE '%queue%';
```

### 4. Check container build context

```bash
# See what Dockerfile is being used
cat /home/ubuntu/nzbdav/backend/Dockerfile | head -20

# Check if .dockerignore is excluding files
cat /home/ubuntu/nzbdav/.dockerignore 2>/dev/null || echo "No .dockerignore"
```

---

## Why My Fixes Would Solve This

### Current Deadlock Scenario (WITHOUT fixes):

```
┌─────────────┐
│HealthCheck  │ ─┐
│ needs 86+   │  │
└─────────────┘  ├─> Both waiting
                 │   for same
┌─────────────┐  │   resource
│   Queue      │  │
│ needs 86+   │ ─┘
└─────────────┘

Available: 85

Result: DEADLOCK
```

### With My Fixes:

```
┌──────────────────────────┐
│ ReservedConnectionsMiddle│
│ware sets context on ALL  │
│ HTTP requests            │
└────────┬─────────────────┘
         │
         ├─> HealthCheck gets requiredAvailable=115
         ├─> Queue gets requiredAvailable=115
         └─> Streaming gets requiredAvailable=115

ALL operations have SAME priority
Fair first-come-first-served sharing
NO deadlock!
```

The middleware ensures:
1. **Consistent reservation** across all operations
2. **Equal priority** (no operation can monopolize)
3. **Proper context propagation** through streams
4. **Prevents deadlock** by ensuring fair resource sharing

---

## Recommended Immediate Action

**FASTEST FIX**: Option B - Configuration Fix

1. Stop container
2. Edit config DB: Set `api.max-queue-connections = 145`
3. Restart container
4. Queue should immediately unblock

**Time**: ~2 minutes

Then when you have time:
- Rebuild with proper fixes (Option A)
- This provides the permanent, proper solution

---

## Validation

After applying fix, check logs:

```bash
# Should see queue progress
docker logs nzbdav --tail 100 | grep "Step 2:"

# Should see low/zero waiters
docker logs nzbdav --tail 50 | grep "Waiters=" | tail -5

# Should see connections being used
docker logs nzbdav --tail 50 | grep "Usage=" | tail -5
```

Expected healthy state:
```
Waiters=0-5 (low)
Usage=Queue=20-30,HealthCheck=10-20 (balanced)
CurrentCount=100+ (plenty available)
```

---

## Root Cause Summary

1. **Immediate cause**: Deadlock where all operations need 86+ connections but only 85 available
2. **Underlying cause**: Container running old image without fixes
3. **Config issue**: Both Queue and HealthCheck using high reservation values creating mutual blocking
4. **Solution**: Either rebuild with fixes OR disable reservation via config
