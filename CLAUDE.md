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

Build multi-arch image:
```bash
docker build -t nzbdav/nzbdav .
```

Run container with config persistence:
```bash
docker run -p 3000:3000 -v $(pwd)/config:/config nzbdav/nzbdav
```

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
- Managed by EF Core migrations

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
