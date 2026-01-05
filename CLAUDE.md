# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NzbDav is a WebDAV server that mounts and streams NZB documents as a virtual file system without downloading. It integrates with Sonarr/Radarr via a SABnzbd-compatible API and enables streaming media directly from Usenet providers through Plex/Jellyfin.

## Architecture

### Dual-Service Architecture

The application runs two processes managed by entrypoint.sh:

1. **Backend (C# .NET 9.0)** - Port 8080
   - ASP.NET Core WebDAV server
   - SABnzbd-compatible API
   - Usenet client and streaming engine
   - SQLite database with EF Core
   - Location: `/backend`

2. **Frontend (React Router + Express)** - Port 3000
   - Server-side rendered React application
   - WebSocket client for real-time updates
   - Proxies API requests to backend
   - Location: `/frontend`

The entrypoint waits for backend health checks before starting frontend, and shuts down both if either exits.

### Backend Core Components

**WebDAV Virtual Filesystem** (`backend/WebDav/`)
- `DatabaseStore` - Root WebDAV store backed by SQLite
- Virtual collections expose NZB contents, completed symlinks, and item IDs
- Streams content directly from Usenet without local storage
- Custom GET/HEAD handlers (`GetAndHeadHandlerPatch`) support range requests for seeking

**Queue System** (`backend/Queue/`)
- `QueueManager` - Background processor with single-threaded queue execution
- `QueueItemProcessor` - Processes NZB files through pipeline:
  1. Deobfuscation (removes obfuscated filenames)
  2. File aggregation (groups RAR parts, multipart files)
  3. File processing (mounts to WebDAV filesystem)
  4. Post-processing (creates symlinks, notifies Sonarr/Radarr)

**Usenet Client** (`backend/Clients/Usenet/`)
- `UsenetStreamingClient` - Connection pooling and segment streaming
- Supports multiple providers with fallback
- Range-request capable for efficient seeking in video streams

**Health Checking** (`backend/Services/HealthCheckService.cs`)
- Background service that validates NZB content availability
- Detects missing segments on Usenet providers
- Triggers Par2 recovery when needed
- Stores results in `HealthCheckResults` and `HealthCheckStats` tables

**Database Schema** (`backend/Database/DavDatabaseContext.cs`)
Key tables:
- `DavItems` - Virtual filesystem hierarchy (directories, files)
- `DavNzbFiles` - NZB file metadata with segment IDs
- `DavRarFile` / `DavMultipartFile` - Archive metadata
- `QueueItems` / `HistoryItems` - SABnzbd queue/history state
- `HealthCheckResults` - Content availability validation results

**SABnzbd API** (`backend/Api/SabControllers/`)
- Compatible subset of SABnzbd API for Sonarr/Radarr integration
- Controllers: Queue, History, Version, Config, AddFile, etc.
- Frontend proxies authenticated requests to backend

### Frontend Architecture

**React Router v7** (`frontend/app/`)
- File-based routing in `app/routes/`
- Server-side rendering via `@react-router/express`
- Routes: queue, health, settings, explore (WebDAV browser), login/logout

**Backend Integration** (`frontend/server.js`)
- Proxies `/api/*` requests to backend with API key injection
- WebSocket proxy for real-time queue progress updates
- Session-based authentication with remix-auth

**Real-time Updates**
- WebSocket connection to `/ws` endpoint on backend
- Topics: queue progress, health check results
- Frontend subscribes and updates UI reactively

## Development Commands

### Backend (.NET)

Build and run backend:
```bash
cd backend
dotnet restore
dotnet build
dotnet run
```

Run database migrations:
```bash
cd backend
dotnet run -- --db-migration
```

Create new migration:
```bash
cd backend
dotnet ef migrations add MigrationName
```

### Frontend (Node/React)

Install dependencies:
```bash
cd frontend
npm install
```

Development server (HMR enabled):
```bash
cd frontend
npm run dev
```

Build for production:
```bash
cd frontend
npm run build          # Build client bundle
npm run build:server   # Build SSR server
```

Type checking:
```bash
cd frontend
npm run typecheck
```

### Docker

**CRITICAL DEVELOPMENT WORKFLOW:**
- **ALWAYS** build using: `docker build -t local/nzbdav:3 .`
- **NEVER** run or restart containers yourself - the user will handle that
- **ALWAYS** test changes by building the Docker image
- After building, inform the user that the image is ready for testing

Build image for testing:
```bash
docker build -t local/nzbdav:3 .
```

Build multi-arch image for production:
```bash
docker build -t nzbdav/nzbdav .
```

Run container (user will do this):
```bash
docker run -p 3000:3000 -v $(pwd)/config:/config local/nzbdav:3
```

### Build Version Updates

**IMPORTANT:** When making significant code changes, update the build version string in `backend/Program.cs` (lines 58-61):

```csharp
Log.Warning("  NzbDav Backend Starting - BUILD v2025-12-30-DB-OPTIMIZATIONS");
Log.Warning("  FEATURE: Database PRAGMA Optimizations (5-10x faster migrations)");
```

**Format:** `BUILD vYYYY-MM-DD-FEATURE-NAME`

**Purpose:** This version appears in Docker logs and helps verify that the correct build is running after container restarts.

**When to update:**
- After implementing new features
- After performance optimizations
- After bug fixes that affect core functionality
- Use the current date and a short descriptor of the main change

## Configuration

**Environment Variables**
- `CONFIG_PATH` - Config directory path (default: `/config`)
- `BACKEND_URL` - Backend URL for frontend proxy (default: `http://localhost:8080`)
- `FRONTEND_BACKEND_API_KEY` - Shared secret between frontend/backend (auto-generated)
- `LOG_LEVEL` - Logging level: Debug, Information, Warning, Error (default: Warning)
- `PUID` / `PGID` - User/group ID for file permissions in Docker
- `MAX_REQUEST_BODY_SIZE` - Max NZB upload size (default: 100MB)
- `DISABLE_FRONTEND_AUTH` - Disable frontend login (for reverse proxy auth)
- `DISABLE_WEBDAV_AUTH` - Disable WebDAV authentication

**Database Location**
- SQLite database: `{CONFIG_PATH}/db.sqlite`
- Development/testing location: `/opt/docker_local/nzbdav/config/db.sqlite`
- Managed by EF Core migrations
- **IMPORTANT:** Run migrations before testing changes: `dotnet run -- --db-migration` (from backend directory)

**Config Storage**
- Settings stored in `ConfigItems` table
- `ConfigManager` provides typed access to settings
- Frontend updates config via `/api/sab?mode=set_config`

## Key Implementation Patterns

**Streaming Architecture**
- NZB segment IDs stored in database, content streamed on-demand
- Range requests enable seeking without downloading entire files
- Archive contents (RAR/7z) extracted via `SharpCompress` streaming API
- Password-protected archives supported via queue metadata

**Symlink Handling**
- Completed items expose `.rclonelink` files pointing to `.ids/{guid}` paths
- RClone translates these to native symlinks when mounting WebDAV
- Sonarr/Radarr move symlinks to media library, preserving streaming

**Multi-Provider Usenet**
- Multiple providers configured with fallback priority
- Connection pooling per provider with configurable limits
- Segment retry logic across providers

**Par2 Recovery** (`backend/Par2Recovery/`)
- Derived from https://bitbucket.org/PLN/pln.infra.parreader
- Used during health checks to repair missing segments
- Validates and repairs damaged NZB content

## Log Analysis

**CRITICAL: Docker logs contain ANSI color codes**

When analyzing Docker logs with grep, you MUST strip ANSI escape sequences first:

```bash
# WRONG - grep will match ANSI codes
docker logs nzbdav 2>&1 | grep "some pattern"

# CORRECT - strip ANSI codes first
docker logs nzbdav 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | grep "some pattern"

# Or use --no-color if available
docker logs --no-color nzbdav 2>&1 | grep "some pattern"
```

**Why this matters:**
- ANSI codes like `[38;5;0242m` can interfere with grep patterns
- Without stripping, searches for exact text may miss matches
- Pattern matching becomes unreliable

**Always strip ANSI codes when:**
- Searching for specific log patterns
- Counting occurrences
- Extracting structured data from logs

## Testing Notes

**Health Check Testing**
- Add test NZB via queue or WebDAV watch folder
- Monitor health page for availability checks
- Verify Par2 recovery triggers on missing segments

**WebDAV Testing**
- Mount WebDAV: `http://localhost:3000` (after configuring credentials)
- Test with RClone: requires `--links` and `--use-cookies` flags
- Verify seeking in video files via Plex/Jellyfin

**Queue Processing**
- Upload NZB via `/queue` page or SABnzbd API
- Monitor WebSocket messages for progress updates
- Check history page for completion status and symlink paths

## Performance Troubleshooting

### Slow Download Speeds

If experiencing slow download speeds (< 10 MB/s), check for these common issues:

**1. Provider Timeouts**
- **Symptom**: Logs show `[BufferedStream] Worker X timed out` or `Timeout in FetchSegmentsAsync`
- **Root Cause**: Provider-level performance issues, NOT system-level problems
  - **Verified**: Network connectivity, DNS resolution, CPU, memory, and Docker networking are all functioning normally
  - **Actual cause**: Specific Usenet providers are slow, geographically distant, throttling, or under heavy load
  - **Evidence**: Some providers work perfectly (0 timeouts) while others consistently timeout on the same segments
- **Diagnosis**:
  ```bash
  # Check for timeout errors with elapsed time
  docker logs nzbdav 2>&1 | grep "timed out" | tail -20

  # Count timeouts by provider
  docker logs nzbdav 2>&1 | grep "timed out" | grep -oP 'provider: \K[^)]+' | sort | uniq -c | sort -rn

  # Check if timeouts hit the configured limit (e.g., 60s)
  docker logs nzbdav 2>&1 | grep "after.*s \(Segment" | grep -oP 'after \K[0-9.]+' | sort -n | tail -10
  ```
- **Solutions**:
  - **Increase operation timeout** (default 60s for segments):
    ```sql
    INSERT OR REPLACE INTO ConfigItems (ConfigName, ConfigValue)
    VALUES ('usenet.operation-timeout', '180');  -- 3 minutes
    ```
  - **Disable problematic provider** temporarily via Settings > Usenet page
  - **Reduce max connections** for slow providers (shifts capacity to faster providers)
  - **Reorder providers** to prioritize faster ones in the provider configuration

**2. Worker Cancellations**
- **Symptom**: Logs show `[BufferedStream] Worker X canceled after processing Y segments`
- **Expected behavior**:
  - For multipart files: Workers may be canceled when seeking across RAR parts (cached for 30s)
  - For sequential playback: Workers should complete all segments without cancellation
- **Problem indicators**:
  - Workers canceled after < 50 segments on sequential download
  - Frequent "Workers have not completed after 2 minutes" warnings
  - Cancellations with "Elapsed: < 1s" (immediate cancellations)
- **Causes**:
  - Provider timeouts (see #1 above)
  - Client disconnecting/seeking frequently
  - Stream cache not working (check CombinedStream code)

**3. Provider Performance Analysis**
- Check provider statistics for a specific file:
  ```sql
  SELECT ProviderIndex, SuccessfulSegments, FailedSegments,
         ROUND(CAST(TotalBytes AS REAL) / TotalTimeMs * 1000 / 1024 / 1024, 2) as SpeedMBps
  FROM NzbProviderStats
  WHERE JobName LIKE '%YourFileName%'
  ORDER BY ProviderIndex;
  ```
- **Good provider**: > 90% success rate, > 1.5 MB/s per connection
- **Problem provider**: < 70% success rate, < 0.5 MB/s, or high timeout frequency

**4. Configuration Tuning**
- **Connections per stream** (Settings > WebDAV):
  - Default: 25 concurrent connections
  - Higher = faster but more memory usage
  - Recommended range: 20-40 connections
- **Stream buffer size** (Settings > WebDAV):
  - Default: 50 segments (actual: Math.max(50, connections * 5))
  - Each segment ~300-500 KB
  - Increase for smoother playback on high-latency connections
- **Provider max connections**:
  - Total across all providers should be 150-250 for optimal performance
  - Distribute based on provider reliability (more to faster providers)

**5. Multipart File Performance**
- **Stream caching**: CombinedStream caches up to 3 recently used parts for 30 seconds
- **Sequential playback**: Should see zero cancellations, full buffer utilization
- **Random seeking**: Expect occasional cache expiration cancellations (normal)
- **Check cache effectiveness**:
  ```bash
  # Look for cache hits vs misses in debug logs
  docker logs nzbdav 2>&1 | grep "CombinedStream\|cache"
  ```
