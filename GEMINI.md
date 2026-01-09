# GEMINI.md

## Project Overview

**NzbDav** is a specialized WebDAV server designed to stream NZB documents as a virtual file system. It enables users to mount NZB files and stream content (including video seeking) directly from Usenet providers without downloading files locally. It is built to integrate seamlessly with the *Arr stack (Sonarr/Radarr) via a SABnzbd-compatible API.

## Architecture

The application operates as a dual-service system within a single container/deployment:

1.  **Backend (`/backend`)**:
    *   **Technology:** C# .NET 9.0 (ASP.NET Core).
    *   **Function:** Core WebDAV server, Usenet client, streaming engine, SQLite database management, and SABnzbd-compatible API.
    *   **Key Libraries:** `NWebDav.Server` (WebDAV), `Microsoft.EntityFrameworkCore.Sqlite` (Database), `SharpCompress` (Archives), `Usenet` (NNTP).
    *   **Port:** 8080 (Internal).

2.  **Frontend (`/frontend`)**:
    *   **Technology:** Node.js, React Router v7, Express, TypeScript.
    *   **Function:** User Interface for management, settings, and exploration. Uses Server-Side Rendering (SSR).
    *   **Key Libraries:** React 19, TailwindCSS, Bootstrap, Remix Auth.
    *   **Port:** 3000 (Public/Exposed).

## Key Directories & Files

*   **`/backend`**: .NET source code.
    *   `NzbWebDAV.csproj`: Project configuration and dependencies.
    *   `Program.cs`: Application entry point.
    *   `Api/`: Controllers for Authentication, Config, Health, WebDAV listing, and the SABnzbd compatibility layer.
    *   `Database/`: EF Core context and models (`DavDatabaseContext.cs`).
    *   `Queue/`: Logic for processing NZB files, deobfuscation, and aggregation.
    *   `WebDav/`: Custom WebDAV store implementation (`DatabaseStore.cs`).
*   **`/frontend`**: React source code.
    *   `app/`: Application source (routes, components, utilities).
    *   `server/`: Express server entry point (`app.ts`, `websocket.server.ts`).
    *   `package.json`: Frontend dependencies and scripts.
*   **`entrypoint.sh`**: Orchestrates container startup, handles user permissions (PUID/PGID), runs migrations, and manages both backend and frontend processes.
*   **`Dockerfile`**: Multi-stage build definition for creating the Docker image.

## Development Workflow

### Docker (Primary Build Method)

**ALWAYS** use this method to build the project to ensure full stack compatibility. **You must always build the Docker container after making code changes.**

*   **Build Image:** `docker build -t local/nzbdav:3 .`
*   **Run Container:**
    ```bash
    docker run -p 3000:3000 \
      -v $(pwd)/config:/config \
      -e PUID=1000 -e PGID=1000 \
      local/nzbdav:3
    ```

**Important:** Never restart the container automatically after building. The user will handle restarts manually. Building the image should be done to verify it builds correctly.

### Backend (.NET) - Local Dev Only

*   **Directory:** `./backend`
*   **Build:** `dotnet build`
*   **Run:** `dotnet run`
*   **Database Migration:** `dotnet run -- --db-migration`
*   **Create Migration:** `dotnet ef migrations add <MigrationName>`

### Frontend (Node/React) - Local Dev Only

*   **Directory:** `./frontend`
*   **Install Dependencies:** `npm install`
*   **Development Server:** `npm run dev` (Starts Vite with HMR)
*   **Build:** `npm run build` (Client) & `npm run build:server` (SSR Server)
*   **Typecheck:** `npm run typecheck`

## Configuration & Environment

*   **`CONFIG_PATH`**: Directory for persistent config and SQLite DB (default: `/config`).
*   **`BACKEND_URL`**: Internal URL for frontend to reach backend (default: `http://localhost:8080`).
*   **`FRONTEND_BACKEND_API_KEY`**: Shared secret for inter-process auth (auto-generated if missing).
*   **`PUID`/`PGID`**: User and Group IDs for file permissions.
*   **`LOG_LEVEL`**: Application logging verbosity (Debug, Information, Warning, Error).

## Core Concepts

*   **Virtual Filesystem**: NZB contents are not downloaded to disk. They are represented virtually in the WebDAV layer. Content is streamed and decrypted/decoded on-the-fly from Usenet.
*   **Symlinks**: Completed "downloads" are actually `.rclonelink` files pointing to internal IDs. RClone (with `--links`) exposes these as real symlinks to the OS/media server.
*   **Health Checks**: Background tasks actively verify segment availability on Usenet and trigger Par2 recovery if needed.

## Gemini Added Memories

- Implemented retry logic with exponential backoff (up to 5 attempts) in ConnectionPool.cs to handle transient socket exhaustion (SocketException/EAGAIN) during high-concurrency tasks like HealthChecks.
- Fixed a bug where deleting history items failed to hide them in the UI. Changed `DavDatabaseClient.RemoveHistoryItemsAsync` to perform a hard delete (`RemoveRange`) instead of a soft delete (`IsHidden`), as requested by the user to ensure items are actually removed from the DB.
- Fixed a UI bug where the "Completed Items" table disappeared completely when filters resulted in zero items. Updated the render condition in `frontend/app/routes/queue/route.tsx` to keep the table (and filters) visible if filters are active (`failureReason`, `!showHidden`).
- Added a "No results found" message row to the History table in `frontend/app/routes/queue/components/history-table/history-table.tsx` for better user feedback when the list is empty.
- Handled `SharpCompress.Common.InvalidFormatException` in `RarProcessor.cs`. It is now caught and re-thrown as `UsenetArticleNotFoundException` to properly mark the download as failed with "Missing Articles" instead of a generic system error.
- Implemented automatic removal of failed history items with the reason "Missing Articles" after a 15-minute delay. This ensures the history table doesn't get cluttered with useless failed items, while still giving Sonarr/Radarr enough time to detect the failure and blacklist the release.
- Added automatic cleanup of successfully imported items from the history table. `ArrMonitoringService` now periodically (every 60 seconds) queries Sonarr/Radarr history for recent "Imported" events and removes the corresponding items from NzbDav's history if they are found. This proactively keeps the history clean even if "Remove Completed" is not configured in the Arrs.
- Added a "Status" filter to the "Completed Items" table in the UI, allowing users to filter items by "Completed" or "Failed" status. Updated the backend `GetHistory` endpoint to support the `status` query parameter.
- Enhanced logging in `FetchFirstSegmentsStep` to report the top 5 slowest files during the probe phase, aiding in performance analysis.
- Changed the initial status of analysis items from "Initializing..." to "Queued" for clarity.
- Grouped queued analysis items in the "Active Analyses" table into a single summary row to prevent clutter when many items are waiting.
- Fixed the "Download NZB" button in the File Details modal for completed items. It now correctly retrieves the NZB content from the `HistoryItem` (where it is archived) instead of looking for the deleted `QueueNzbContents`.
- Added fallback NZB generation logic. If the original NZB content is missing (e.g. history item deleted), the system now regenerates a valid NZB on the fly using the stored `DavNzbFile` segment metadata, ensuring download availability for all files in the library.
- Expanded NZB fallback generation to support RAR-backed (`DavRarFile`) and Multipart (`DavMultipartFile`) files. The generated NZB reconstructs the file parts (e.g. `.part01.rar`) with `bytes="0"` since segment sizes are not persisted for these types, allowing recovery via NZBGet even for complex releases.
- Fixed an infinite retry loop in `HealthCheckService`. When an urgent health check timed out repeatedly, it wasn't updating the `NextHealthCheck` timestamp, causing it to retry immediately forever. It now correctly backs off for 1 day after multiple failures.
- Optimized `HealthCheckService` to process health checks in parallel (up to 3 files concurrently). This prevents a single slow or large file from blocking the entire repair queue, significantly improving throughput and responsiveness for large libraries.
- Fixed `System.Text.Json.JsonException: '0x09' is invalid within a JSON string` in `DavDatabaseContext.cs` by sanitizing JSON strings (replacing raw tabs `\t` with escaped tabs `\\t`) before deserialization in `ValueConverter` for `DavNzbFile`, `DavRarFile`, and `DavMultipartFile`. This resolves crashes when reading corrupted segment data from the database.
