# GEMINI.md

## Project Overview

This is a web application called NzbDav, which acts as a WebDAV server to mount and browse NZB documents as a virtual file system. It's designed to integrate with media management tools like Sonarr and Radarr.

The project is composed of a .NET Core backend and a React frontend.

**Backend:**

*   **Framework:** ASP.NET Core (`net9.0`)
*   **Database:** Entity Framework Core with SQLite.
*   **Key Dependencies:** `NWebDav.Server`, `Serilog`, `SharpCompress`, `Usenet`

**Frontend:**

*   **Framework:** React
*   **Build Tool:** Vite
*   **Server:** Express
*   **Routing:** React Router
*   **Styling:** Bootstrap and Tailwind CSS

## Building and Running

### Docker (Recommended)

The easiest way to run the application is with Docker:

```bash
docker run --rm -it -p 3000:3000 nzbdav/nzbdav:alpha
```

To persist settings, mount a volume at `/config`:

```bash
mkdir -p $(pwd)/nzbdav && \
docker run --rm -it \
  -v $(pwd)/nzbdav:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -p 3000:3000 \
  nzbdav/nzbdav:alpha
```

### Development

**Backend:**

To run the backend for development, you'll need the .NET 9 SDK.

```bash
cd backend
dotnet run
```

**Frontend:**

To run the frontend for development, you'll need Node.js and npm.

```bash
cd frontend
npm install
npm run dev
```

## Development Conventions

The project uses a conventional structure for both the frontend and backend.

**Backend:**

*   The backend follows the standard ASP.NET Core project structure.
*   Controllers are located in `backend/Api/Controllers`.
*   Database models are in `backend/Database/Models`.

**Frontend:**

*   The frontend is a standard Vite-based React application.
*   Source code is in the `frontend/app` directory.
*   The Express server is defined in `frontend/server.ts`.
