# syntax=docker/dockerfile:1.4
#
# Standalone image for the nzbdav2 -> infinidysk migration tool. Self-contained publish (bundles
# its own .NET runtime) onto the minimal `runtime-deps` base - no SDK, and not even the regular
# `dotnet/runtime` image, is needed to run it. Built and pushed by
# .github/workflows/docker-publish.yml alongside the main nzbdav2 image, as
# ghcr.io/dgherman/nzbdav2-migrate-infinidysk.
#
# Usage: see MIGRATING_TO_INFINIDYSK.md. In short:
#   docker run --rm \
#     -v /path/to/nzbdav2/config:/source:ro \
#     -v /path/to/infinidysk/config:/target \
#     ghcr.io/dgherman/nzbdav2-migrate-infinidysk:latest \
#     --source /source --target /target [--apply]

# -------- Stage 1: publish --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src
COPY . .

# Derive .NET RID arch suffix the same way the main app's Dockerfile does - uname -m on the
# (possibly QEMU-emulated) build platform, not Docker's less reliable TARGETARCH.
RUN set -eux; \
    case "$(uname -m)" in \
      x86_64)  DOTNET_ARCH=x64 ;; \
      aarch64) DOTNET_ARCH=arm64 ;; \
      *) echo "Unsupported arch: $(uname -m)" && exit 1 ;; \
    esac; \
    echo "Publishing self-contained for linux-musl-${DOTNET_ARCH}"; \
    dotnet publish MigrateToInfinidysk.csproj -c Release \
      -r "linux-musl-${DOTNET_ARCH}" \
      --self-contained true \
      -p:PublishSingleFile=false \
      -o /app/publish

# -------- Stage 2: runtime --------
# runtime-deps (not the full `dotnet/runtime` image): a self-contained publish bundles its own
# .NET runtime, so only the OS-level native dependencies (libc, ICU, the SQLite native library
# Microsoft.Data.Sqlite needs) are required underneath it.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine

WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["./MigrateToInfinidysk"]
