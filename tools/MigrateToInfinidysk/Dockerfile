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
# Pinned to $BUILDPLATFORM so the SDK/build tooling itself runs natively on the CI runner (fast,
# no QEMU emulation of the whole toolchain) even when cross-publishing for a different target
# architecture - `dotnet publish -r <RID>` cross-compiles the *output* just fine from a native
# host; it's only the RID selection that must track the actual target, not the build host.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

# Buildx populates TARGETARCH (amd64/arm64/...) per platform being built, even inside a stage
# pinned to $BUILDPLATFORM - unlike `uname -m`, which reports the (native, unemulated) build
# host's own architecture regardless of which platform is actually being built. Using uname -m
# here previously made every platform in a `--platform linux/amd64,linux/arm64` build publish
# x64 binaries when built on an x64 runner (the SDK stage always executed on the runner's native
# amd64, so uname -m was always x86_64), silently breaking the published ARM64 image - its musl
# loader can't run x86_64 binaries and the container exits 255 on real ARM64 hardware.
ARG TARGETARCH

WORKDIR /src
COPY . .

RUN set -eux; \
    case "$TARGETARCH" in \
      amd64) DOTNET_ARCH=x64 ;; \
      arm64) DOTNET_ARCH=arm64 ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac; \
    echo "Publishing self-contained for linux-musl-${DOTNET_ARCH} (TARGETARCH=$TARGETARCH)"; \
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
