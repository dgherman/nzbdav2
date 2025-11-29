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

### Backend (.NET)

*   **Directory:** `./backend`
*   **Build:** `dotnet build`
*   **Run:** `dotnet run`
*   **Database Migration:** `dotnet run -- --db-migration`
*   **Create Migration:** `dotnet ef migrations add <MigrationName>`

### Frontend (Node/React)

*   **Directory:** `./frontend`
*   **Install Dependencies:** `npm install`
*   **Development Server:** `npm run dev` (Starts Vite with HMR)
*   **Build:** `npm run build` (Client) & `npm run build:server` (SSR Server)
*   **Typecheck:** `npm run typecheck`

### Docker (Full Stack)

*   **Build Image:** `docker build -t nzbdav/nzbdav .`
*   **Run Container:**
    ```bash
    docker run -p 3000:3000 \
      -v $(pwd)/config:/config \
      -e PUID=1000 -e PGID=1000 \
      nzbdav/nzbdav
    ```

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
