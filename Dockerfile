# syntax=docker/dockerfile:1.4

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:alpine AS frontend-build

WORKDIR /frontend
COPY ./frontend ./

RUN npm install
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build

WORKDIR /backend
ARG CACHE_BUST
COPY ./backend ./

# Derive .NET RID arch suffix from the build platform (uname -m)
# Docker's TARGETARCH can be unreliable on macOS hosts; using uname -m directly
RUN set -eux; \
    case "$(uname -m)" in \
      x86_64)  DOTNET_ARCH=x64 ;; \
      aarch64) DOTNET_ARCH=arm64 ;; \
      *) echo "Unsupported arch: $(uname -m)" && exit 1 ;; \
    esac; \
    echo "Building for linux-musl-${DOTNET_ARCH}"; \
    dotnet restore; \
    dotnet publish -c Release -r "linux-musl-${DOTNET_ARCH}" -o ./publish

# -------- Stage 3: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

WORKDIR /app

# Prepare environment
RUN mkdir /config \
    && apk add --no-cache nodejs npm libc6-compat shadow su-exec bash curl sqlite ffmpeg

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /backend/publish ./backend

# Entry and runtime setup
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 3000
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning

# .NET Garbage Collection defaults — tuned for memory-constrained deployments.
# These can be overridden in your docker-compose.yml environment section.
#
# DOTNET_GCServer: GC mode.
#   0 = Workstation GC — aggressive collection, returns memory to OS quickly.
#       Best for 1-2GB RAM systems.
#   1 = Server GC — holds onto memory for throughput. Better for 4GB+ systems.
#
# DOTNET_GCHeapHardLimit: Max managed heap size in hex bytes.
#   Prevents unbounded memory growth. GC collects more aggressively near the limit.
#   Examples: 0x20000000 = 512MB, 0x40000000 = 1GB, 0x80000000 = 2GB
#
# Recommended configurations:
#   1GB VPS:  DOTNET_GCServer=0  DOTNET_GCHeapHardLimit=0x20000000  (512MB)
#   2GB VPS:  DOTNET_GCServer=0  DOTNET_GCHeapHardLimit=0x40000000  (1GB)
#   4GB+ NAS: DOTNET_GCServer=1  DOTNET_GCHeapHardLimit=0x80000000  (2GB)
ENV DOTNET_GCServer=0
ENV DOTNET_GCHeapHardLimit=0x20000000

CMD ["/entrypoint.sh"]
