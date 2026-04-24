# nzbdav2

nzbdav2 is a WebDAV server that allows you to mount and stream NZB content as a virtual file system without downloading. It integrates with Sonarr and Radarr via a SABnzbd-compatible API and enables streaming directly from Usenet providers through Plex or Jellyfin — using no local storage.

> **Provenance:** nzbdav2 is an independent project based on [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). During early development, some changes were incorporated from [johoja12/nzbdav](https://github.com/johoja12/nzbdav) (a separate fork of the same upstream, now private). nzbdav2 is not a continuation of that project and is developed and maintained independently.

Throughout this document, "upstream" refers to [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav).

## What nzbdav2 Adds

These features are original to nzbdav2 and not present in [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav).

### BufferedSegmentStream

A producer-consumer RAM jitter buffer that pre-fetches NZB segments in parallel, handles out-of-order arrival, and re-orders them before writing to the output stream. Includes straggler detection: if the head-of-line segment stalls for more than 1.5s, a duplicate fetch races it on another connection. Upstream uses a sequential `MultiSegmentStream` with no buffering. The RAM buffer isolates the player from network jitter and eliminates stutter at high bitrates.

### Persistent Seek Cache

Segment byte offsets are cached in the database during health checks and media analysis. This enables O(log N) instant seeking for previously accessed files — no NNTP round-trips needed. Without the cache, each seek requires interpolation searches across the provider. Upstream has no equivalent.

### Priority Queuing via `GlobalOperationLimiter`

Connections are statically partitioned between active streaming requests and background tasks (health checks, queue processing). This prevents background work from starving playback connections. Upstream uses `PrioritizedSemaphore` with dynamic probability-based allocation; nzbdav2's static partitioning is simpler and sufficient for the target use case.

### Audio File Support

nzbdav2 recognizes audio file extensions and accepts audio-only NZBs as valid imports. `EnsureImportableMediaValidator` validates for both video and audio files. Default SABnzbd categories include `audio`. Upstream is video-only — audio NZBs would be rejected.

### Provider Stats UI

Real-time per-provider performance tracking visible from the Stats page: throughput (MB/s), success rate, active connection usage, and the file currently being served per connection. No upstream equivalent.

### Media Analysis via ffprobe

Deep media verification triggered on demand (from File Details modal) or automatically during health checks when a file lacks media metadata. Displays video and audio codec information in the File Details modal. ffmpeg and ffprobe are bundled in the Docker image. No upstream equivalent.

### Rich File Details Modal

A per-file action panel accessible from Health, Stats, and Explore pages. Provides: run health check, trigger repair (delete + Sonarr/Radarr re-search), run media analysis, test download, and view per-provider stats. Upstream's equivalent is a simpler dropdown with Preview, Download, and Export NZB.

## Adopted Upstream Features — Architectural Differences

These features originated in [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) but are implemented differently in nzbdav2. Implementation rationale is documented in [`docs/upstream-sync-2026-03-10.md`](./docs/upstream-sync-2026-03-10.md).

### Zstd NZB Compression (In-DB Instead of Blobstore)

Upstream's v0.6.0 migration moves NZB XML content to a filesystem blobstore. nzbdav2 skipped the blobstore and instead applies Zstd compression to NZB content stored in-DB via an EF Core value converter. This achieves approximately 31% database size reduction (856MB → 592MB after VACUUM) without a non-reversible schema migration. The blobstore migration was evaluated and skipped; see the sync doc for the full rationale.

### RcloneRcService (DI-Injected vs. Static)

Upstream uses a static `RcloneClient` class with flat config keys (`rclone.host`, `rclone.user`, `rclone.pass`). nzbdav2 uses a DI-injected `RcloneRcService` singleton backed by `IHttpClientFactory` with a single JSON config blob at the `rclone.rc` key. nzbdav2 also adds `DeleteFromDiskCache` — explicit RClone VFS cache invalidation — which upstream does not have.

## Deliberately Skipped Upstream Features

These upstream features were evaluated and intentionally not adopted. The "Re-evaluate If" column documents when to revisit each decision. Full rationale is in [`docs/upstream-sync-2026-03-10.md`](./docs/upstream-sync-2026-03-10.md).

| Feature | Why Skipped | Re-evaluate If |
|---|---|---|
| Blobstore migration | Non-reversible schema change. In-DB Zstd compression achieves the same size savings safely. | DB size becomes a problem again, or upstream makes blobstore reversible. |
| Export NZB from Explore | Depends on blobstore (`nzbBlobId`). Not applicable without blobstore. | Could be reimplemented to read from in-DB `NzbContents` if the feature is wanted. |
| User-Agent configuration | NNTP protocol does not support user-agent headers. The setting has no effect. | N/A — not a real feature. |
| Explore page actions dropdown | Superseded by the richer `FileDetailsModal`, which provides a superset of actions. | N/A — already covered. |
| `PrioritizedSemaphore` | `GlobalOperationLimiter` meets current needs. Adopting this requires a significant refactor of `GlobalOperationLimiter` and `ConnectionPool`. | Static partitioning causes observed contention issues under load. |
| `UnbufferedMultiSegmentStream` | Not needed. Would serve as a low-memory fallback for `BufferedSegmentStream`. | A low-memory deployment scenario becomes relevant. |

## Architecture Overview

For full architecture, DB schema, development commands, and configuration details, see [`CLAUDE.md`](./CLAUDE.md) — the project's primary architecture reference and AI context file.

### Dual-Service Setup

The application runs two processes managed by `entrypoint.sh`:

- **Backend** (`/backend`) — .NET 10.0 ASP.NET Core on port 8080. WebDAV server, SABnzbd-compatible API, Usenet client, streaming engine, SQLite database via EF Core.
- **Frontend** (`/frontend`) — React Router v7 with server-side rendering, Express proxy on port 3000. Proxies API requests to backend, serves SSR pages, WebSocket connection for real-time updates.

`entrypoint.sh` health-gates frontend startup: it waits for the backend health endpoint before starting the frontend, and shuts down both processes if either exits.

### Streaming Pipeline

NZB segment IDs are stored in the database when an NZB is queued. When a WebDAV request arrives, the backend streams segment data on demand via `BufferedSegmentStream` — no content is stored locally. Range requests are supported, enabling seeking. Archive contents (RAR/7z) are extracted via the `SharpCompress` streaming API without writing to disk.

### WebDAV Virtual Filesystem

NZB contents are exposed as a virtual directory hierarchy. Completed items expose `.rclonelink` files that RClone translates to native symlinks when mounting the WebDAV server. Sonarr and Radarr pick up these symlinks, move them to the media library, and Plex/Jellyfin stream through them — all without any local copy of the content.

### Health and Repair

`HealthCheckService` runs in the background, validating article availability on each configured Usenet provider. When segments are missing, it triggers Par2 recovery or a Sonarr/Radarr re-search (blacklist + new grab). `GlobalOperationLimiter` ensures health check connections never starve active streaming connections.

### SABnzbd-Compatible API

The backend exposes a SABnzbd-compatible API subset (`/api?mode=...`). Sonarr and Radarr are configured to use nzbdav2 as their download client. When they send an NZB, nzbdav2 mounts it to the WebDAV filesystem and signals completion — no actual download occurs.

## Quick Start

```bash
mkdir -p $(pwd)/nzbdav2 && \
docker run --rm -it \
  -v $(pwd)/nzbdav2:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -p 3000:3000 \
  ghcr.io/dgherman/nzbdav2:latest
```

After starting, navigate to `http://localhost:3000` and open Settings to configure your Usenet provider connections.

For all environment variables and configuration options, see [`CLAUDE.md`](./CLAUDE.md).

For a complete deployment guide including RClone mount configuration, Sonarr/Radarr integration, and Docker Compose setup, see the [upstream README](https://github.com/nzbdav-dev/nzbdav#readme). nzbdav2-specific architectural differences (e.g., in-DB Zstd compression instead of blobstore) are documented in [`CLAUDE.md`](./CLAUDE.md) and [`docs/upstream-sync-2026-03-10.md`](./docs/upstream-sync-2026-03-10.md).

## Upstream Sync

nzbdav2 tracks [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) and periodically cherry-picks relevant upstream changes manually. Each sync documents which changes were adopted, which were skipped, and the rationale for each decision. Sync history is in [`docs/upstream-sync-*.md`](./docs/). The most recent file contains the last reviewed upstream commit and a table of all items evaluated.

## Changelog

## v0.6.Z (2026-04-24)
*   **Logging**: Demoted benign cancellation noise. `ConnectionPool` now logs `Connection ... was canceled` at Debug instead of Warning when the caller's `CancellationToken` is the trigger (HTTP client disconnect for `BufferedStreaming`, per-file deadline for `QueueAnalysis`, queue cancellation, etc.). True connection failures still log at Warning/Error and trip the circuit breaker as before. `[CancellationTokenContext]` nested-scope removals (where an inner `SetScopedContext` already cleared the entry) are also Debug now.
*   **Fix**: Queue Step 3 (smart article probe) per-file timeout bumped from 15s to 30s in `QueueItemProcessor.cs`. The previous 15s deadline was tighter than `UsenetStreamingClient.CreateNewConnection`'s own 60s connection timeout and frequently fired during TCP+TLS+NNTP greeting on geographically distant providers (e.g. Frugal AU for non-AU users), producing `[ConnectionPool][...] Failed to create connection for QueueAnalysis: Connection to usenet host (...) was canceled.` for every file in a queue item touching that provider. The 180s overall Step 3 budget is unchanged.
*   **Fix**: V1 → v2 blobstore migration no longer dies with `System.OutOfMemoryException` partway through (~6.5k of ~25k items on a 4 GB container). Two leaks were stacked: (1) `BlobStoreReader.TryReadAsync` allocated a second full-size copy of every decompressed payload via `MemoryStream.ToArray()` — now reads directly from `GetBuffer()` so peak transient memory per blob is roughly halved; (2) the migration loop in `Program.cs` Add()ed every recovered `DavNzbFiles`/`DavRarFiles`/`DavMultipartFiles` row to the EF change tracker but never cleared it between batches, so by item 25k the tracker held all 25k entities simultaneously. Now calls `ChangeTracker.Clear()` after each `SaveChangesAsync` and triggers a non-blocking compacting GC between batches to defragment the LOH (every decompressed buffer is >85 KB). Batch size also reduced from 500 → 100 for additional headroom.
*   **Fix**: V1 blobstore deserialization now uses MemoryPack `[MemoryPackable(GenerateType.VersionTolerant)]` on every shim POCO (`UpstreamDavNzbFile`, `UpstreamDavRarFile`/`UpstreamRarPart`, `UpstreamDavMultipartFile`/`UpstreamMultipartMeta`/`UpstreamFilePart`, `UpstreamAesParams`) and models `UpstreamLongRange` as a `partial record` (not `struct`) — this matches how upstream's `BlobStore.WriteBlob<T>` actually serialised the blobs. Previously every blob read failed with `MemoryPackSerializationException: property count is 2 but binary's header marked as N`, leaving 25k+ items orphaned during migration.
*   **Fix**: V1 → v2 metadata migration now uses the v1 `db.sqlite` (placed at `{CONFIG_PATH}/backup/db.sqlite` or `{CONFIG_PATH}/v1-backup.sqlite`) to recover the `DavItem.Id → FileBlobId` mapping that this fork lost when the `FileBlobId` column was dropped. Each candidate `DavItem` is resolved via that map → `{CONFIG_PATH}/blobs/{first2}/{next2}/{FileBlobId}` → deserialized via Zstd+MemoryPack into the equivalent `DavNzbFiles` / `DavRarFiles` / `DavMultipartFiles` row. Without the v1 backup file present, items remain orphaned and a clear startup warning explains how to provide it. Resolves the `FileNotFoundException: Could not find nzb file with id: …` errors when streaming symlinks pointing into `.ids/`.
*   **Reliability**: `NormalizeLegacyDavItemTypesAsync` no longer unconditionally promotes `Type=2 SubType=201/202/203` items to v2 file types — promotion is now gated on the corresponding metadata row existing.
*   **Logging**: Startup logs now report `[BlobstoreMigration]` progress and an `[OrphanReport]` summary of any remaining unrecoverable items (with sample paths).
*   **Performance**: Bound `BufferedSegmentStream` prefetch to the requested HTTP `Range` end byte (plus a 4-segment overshoot) instead of always queueing every segment to EOF. Prevents ~40 MB of speculative Usenet reads per ranged request — the root cause of slow Radarr imports, ffprobe seek storms, and backend `OutOfMemoryException` on the SAB `mode=history` endpoint when many bounded reads (rclone vfs-cache fills, HDR ffprobe `GetFrameJson` seeks) were in flight concurrently.
*   **Performance**: Skip the shared-stream pump for requests with a closed `bytes=X-Y` range — discrete bounded reads now take the direct bounded-prefetch path instead of attaching to a streaming pump that would prefetch to EOF for downstream readers.
*   **Reliability**: Open-ended (`bytes=X-`) and unbounded GETs are unchanged — Plex/Jellyfin/rclone full-file streaming continues to use the shared streaming pump with full read-ahead.

## v0.6.Z (2026-04-23)
*   **Fix**: Resolve "NOT NULL constraint failed: DavItems.SubType" error when migrating from v1 databases. The compat layer now recreates the DavItems table with nullable SubType column to allow new item creation during queue processing.
*   **Fix**: Corrected `DavItems` schema compatibility rebuild to avoid creating an unintended foreign key from `DavItems.HistoryItemId` to `HistoryItems.Id`, which caused queue item saves to fail with `FOREIGN KEY constraint failed`.
*   **Reliability**: Added pre-migration drift handling for `20260408180402_ChangeQueueItemsFileNameIndexToCategoryFileName` so pre-existing `IX_QueueItems_Category_FileName` no longer crashes startup migrations.
*   **Reliability**: Added startup self-heal to detect and remove the unintended `DavItems.HistoryItemId -> HistoryItems.Id` foreign key on already-affected databases.
*   **UI**: Restored queue pagination controls and queue search UI rendering on the Queue page so large queues no longer fall back to the old unpaged "show everything" behavior.
*   **Logic**: Queue priority-change refresh now preserves current `start`, `limit`, and `search` parameters instead of forcing a `limit=100` snapshot, preventing pagination state resets after moving items.
*   **Fix**: Queue removals now trigger a paginated server refresh (with page clamping) so deleting an entire page immediately pulls in the next items instead of showing a temporary empty queue until manual reload.
*   **UI**: Queue empty-state rendering now considers total queue count, preventing false "nothing left" flashes while the next page is being fetched after bulk deletes.
*   **Reliability**: Queue delete requests from the web UI now use strict mode so backend removal failures are returned as errors instead of being silently reported as success, preventing items from disappearing and then reappearing after refresh.
*   **Fix**: Hardened WebSocket message parsing in the queue UI to ignore malformed/non-JSON frames instead of throwing client-side runtime errors in production chunks, improving real-time queue stability.

## v0.7.22 (2026-04-23)
*   **Fix**: Added v1-compatible `DavItems` type normalization for databases that use `Type=2` + `SubType` (`201/202/203`) so files are no longer misclassified as folders in UI/WebDAV/rclone mounts.
*   **Fix**: Added queue recovery from legacy blob files at startup (`/config/blobs/{firstTwo}/{nextTwo}/{guid}`) to repopulate `QueueNzbContents` before orphan cleanup.
*   **Reliability**: Extended startup migration self-healing to handle mixed-schema upgrades where both old and new type encodings may coexist.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-23-V1-MIGRATION-FILETYPE-QUEUE-RECOVERY`.

## v0.7.21 (2026-04-22)
*   **Fix**: Added pre-migration index compatibility checks that recreate legacy indexes before EF migration runs, preventing failures such as `no such index: IX_DavItems_Type_NextHealthCheck_ReleaseDate_Id` on drifted v1 databases.
*   **Reliability**: Migration bootstrap now safely pre-creates `IX_DavItems_Type_NextHealthCheck_ReleaseDate_Id` and `IX_QueueItems_FileName` only when required tables and columns exist.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-MIGRATION-INDEX-COMPAT`.

## v0.7.20 (2026-04-22)
*   **Fix**: Added runtime queue migration self-healing to backfill `QueueNzbContents` from legacy `QueueItems.NzbContents` when available and remove orphan queue rows that would otherwise stall processing with "no NZB contents" warnings.
*   **Fix**: Added runtime normalization for legacy `DavItems` rows incorrectly marked as directories even though they map to file tables (`DavNzbFiles`, `DavRarFiles`, `DavMultipartFiles`).
*   **Reliability**: Startup compatibility pass now repairs schema/data drift for v1-to-v2 migrations before background services run.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-MIGRATION-COMPAT-PATCHES`.

## v0.7.19 (2026-04-22)
*   **Fix**: Added runtime schema compatibility checks that automatically add missing `DownloadDirId` columns to `HistoryItems` and `HistoryCleanupItems` when upgrading from legacy databases with migration drift.
*   **Reliability**: Added startup self-healing index creation for `IX_HistoryItems_Category_DownloadDirId` to keep history cleanup and health-check queries stable after migration from v1.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-SCHEMA-COMPAT-HISTORY`.

## v0.7.18 (2026-04-22)
*   **Tooling**: Kept Docker publishing repository-derived so GitHub Actions publishes to the current fork owner automatically.
*   **UI**: Updated in-app GitHub and changelog links to point to FizzWhirl/nzbdav2.
*   **Docs**: Updated the Quick Start image reference to `ghcr.io/fizzwhirl/nzbdav2:latest`.

## v0.7.17 (2026-04-22)
*   **Optimization**: Reduced media-analysis decode sample duration from 5 seconds to 2 seconds for Step 5 decode checks to lower analysis data usage.
*   **Logic**: Disabled buffered segment streaming only for `X-Analysis-Mode` requests, so analysis no longer prefetches extra Usenet data while regular playback keeps existing buffering behavior.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-ANALYSIS-LOW-DATA`.

## v0.7.16 (2026-04-22)
*   **Fix**: Step 5 media analysis now keeps files (instead of marking them corrupt) when decode check failures are caused by transient provider errors — 5XX responses, NNTP protocol errors, premature EOF, or decode timeouts. Only genuine codec errors (invalid data, CRC failures) result in corruption marking.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-TRANSIENT-DECODE-FIX`.

## v0.7.15 (2026-04-22)
*   **Reliability**: Hardened `NzbProviderAffinity` stats persistence by preventing overlapping timer writes and reducing write cadence from 5s to 15s.
*   **Fix**: Added retry handling for transient SQLite write errors (`disk I/O error`, `database is locked`) during affinity stats persistence.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-AFFINITY-PERSIST-FIX`.

## v0.7.14 (2026-04-22)
*   **Reliability**: Raised default `analysis.max-concurrent` from `1` to `3` in backend and frontend defaults to avoid severe queue-analysis bottlenecks on large packs.
*   **Logic**: Kept unified analysis fan-out model where both Queue Step 3 smart probe and Step 5 media analysis follow `analysis.max-concurrent`.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-ANALYSIS-DEFAULTS-3`.

## v0.7.13 (2026-04-22)
*   **Logic**: Queue Step 5 media analysis (`ffprobe` + decode checks) now uses `analysis.max-concurrent` instead of a separate hardcoded parallelism value.
*   **Logic**: Queue Steps 3 and 5 now share the same frontend-exposed analysis concurrency setting.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-UNIFIED-ANALYSIS-CONCURRENCY`.

## v0.7.12 (2026-04-22)
*   **Logic**: Queue Step 5 media probing (`ffprobe` + decode checks) now runs only when `api.ensure-article-existence=true`.
*   **Logic**: Queue Step 3 smart article probe now skips sample files when `usenet.hide-samples=true`.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-PROBE-GATING-SAMPLES`.

## v0.7.11 (2026-04-22)
*   **Logic**: Removed the smart article probe safety clamp so Queue Step 3 now uses `analysis.max-concurrent` directly.
*   **Reliability**: Keeps probe concurrency behavior fully aligned with the single analysis tuning setting.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-ANALYSIS-PROBE-UNCAPPED`.

## v0.7.10 (2026-04-22)
*   **Logic**: Queue Step 3 smart article probe parallelism now follows `analysis.max-concurrent` instead of a separate hardcoded value.
*   **Reliability**: Applied safety clamp (`1..8`) to probe parallelism to avoid runaway fan-out while still allowing tuning through a single setting.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-ANALYSIS-PROBE-LINK`.

## v0.7.9 (2026-04-22)
*   **Fix**: Reduced queue Step 3 per-file smart article probe parallelism from 8 to 4 (`Parallel.ForEachAsync MaxDegreeOfParallelism`) to lower concurrent `QueueAnalysis` pressure during large queue processing.
*   **Reliability**: Helps reduce memory pressure spikes when many files are probed simultaneously in the same NZB job.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-22-QUEUE-PROBE-CAP-4`.

## v0.7.8 (2026-04-21)
*   **Fix**: Changed media decode integrity sampling points from 75%/90% to 10%/90% so corruption near the start of files is validated earlier.
*   **Reliability**: Hardened Settings defaults by setting `analysis.max-concurrent` to `1` in the frontend defaults/fallback to match backend safe defaults and reduce OOM risk during mass analysis.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-21-OOM-HARDENING-PASS2`.

## v0.7.7 (2026-04-21)
*   **Fix**: Reverted configurable `usenet.cleanup-timeout-ms` setting and restored fixed 500ms NNTP cleanup timeout behavior.
*   **UI**: Removed "Connection Cleanup Timeout (ms)" from Settings → Usenet.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-21-ROLLBACK-CLEANUP-TIMEOUT`.

## v0.7.6 (2026-04-21)
*   **UI**: Updated Queue page Provider Stats card styling to use Bootstrap theme variables so text/background contrast renders correctly in dark mode.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-21-CLEANUP-TIMEOUT-QUEUE-DARKMODE` for deployment verification.

## v0.7.5 (2026-04-21)
*   **Feature**: Added configurable `usenet.cleanup-timeout-ms` setting to control how long NNTP connection cleanup waits before force-replacing a draining connection.
*   **Reliability**: Wired cleanup timeout configuration into both stream-dispose and background cleanup paths to reduce false cleanup cancellation churn on slower providers.
*   **UI**: Added "Connection Cleanup Timeout (ms)" field to Settings → Usenet with validation and save tracking.

## v0.7.4 (2026-04-21)
*   **Fix**: Reduced default `analysis.max-concurrent` from 3 to 1, and `usenet.max-concurrent-buffered-streams` from 4 to 2, to prevent OOM crashes under memory pressure when multiple ffprobe analyses and buffered streams run simultaneously.
*   **Reliability**: Each ffprobe analysis opens a `NzbFileStream` with a 20-segment prefetch buffer; reducing concurrent analyses from 3 to 1 cuts peak streaming RAM by ~60% for containers with limited memory.

## v0.7.3 (2026-04-21)
*   **Fix**: Added queue-time sample file filtering when `usenet.hide-samples=true`, so sample releases are removed before importable-media validation and ffprobe analysis.
*   **Reliability**: Replaced narrow `".sample."` matching with centralized filename token detection, improving sample filtering across WebDAV content and completed-symlink views.
*   **Logging**: Updated backend startup build banner to `BUILD v2026-04-21-SAMPLE-FILTER-HARDENING` for clearer deployment verification.

## v0.7.2 (2026-04-21)
*   **Fix**: Preview endpoints now reject non-file DavItem IDs up front (directories/roots), preventing HLS/remux requests from targeting invalid `/view` paths.
*   **Reliability**: HLS and remux preview streaming no longer return empty HTTP 200 responses when `ffmpeg` exits with an error before emitting data; zero-output failures now return explicit 502 errors.
*   **Maintenance**: Rebased feature/media-analysis-optimization onto latest `origin/main` and retained branch functionality on top of upstream queue/concurrency changes.
*   **Fix**: Hardened remux stdin handling for early ffmpeg exits (broken pipe/disposed stream paths) to avoid uncontrolled error propagation.
*   **Reliability**: Migrated media analysis ffprobe/ffmpeg invocations to `ProcessStartInfo.ArgumentList` to eliminate fragile argument-string quoting on unusual file paths.
*   **Performance**: Refactored queue Step 5 analysis history persistence from per-item locked `SaveChanges()` to batched post-loop async persistence.
*   **Docs**: Added deep review report plus performance/security addendum in `docs/superpowers/plans/deep-review-report-2026-04-21.md`.

## v0.7.1 (2026-04-13)
*   **Feature**: Hybrid connection pool — replace hard-partitioned connection semaphores with priority-based shared pool (`PrioritizedSemaphore`). Queue processing uses full connection capacity when not streaming; streaming gets guaranteed reserve slots (configurable via `usenet.streaming-reserve`, default 5) and priority scheduling (configurable via `usenet.streaming-priority`, default 80%).
*   **Feature**: Buffered multi-connection streaming during RAR header parsing via new `QueueRarProcessing` context — RAR end-of-archive seeks now pre-fetch segments instead of one-at-a-time lazy fetches.
*   **Feature**: Per-queue-item article caching — segments fetched in Step 1 (first-segment identification) are cached to temp files and reused in Step 2 (RAR header parsing) without additional network round-trips.
*   **Feature**: Queue concurrency caps raised from 1 to `GetMaxDownloadConnections() + 5` — the `PrioritizedSemaphore` is the real gate now.
*   **Config**: New settings: `usenet.streaming-reserve` (default 5), `usenet.streaming-priority` (default 80), `usenet.max-download-connections` (default min(totalPooled, 15)).
*   **Config**: `api.max-queue-connections` deprecated — logs warning if explicitly set; no longer controls a semaphore.

## v0.7.0 (2026-04-12)
*   **Feature**: Shared stream system — multiple concurrent HTTP requests for the same file now share a single `BufferedSegmentStream` instead of each creating their own. When Stremio sends parallel probe + streaming requests, only the first creates the underlying Usenet stream; subsequent requests attach as readers to the same ring buffer. Includes configurable grace period (default 10s) to keep the stream alive between reader transitions, and configurable buffer size (default 32MB).
*   **Feature**: Concurrent stream cap — limits how many `BufferedSegmentStream` instances can exist simultaneously (configurable via `usenet.max-concurrent-buffered-streams`, default 2). Prevents retry storms from spawning unbounded streams that exhaust memory.
*   **Feature**: GC tuning for memory-constrained deployments — Dockerfile now sets `DOTNET_GCConserveMemory=9` and `DOTNET_GCHeapHardLimit` to keep the .NET GC aggressive about reclaiming memory in Docker containers.
*   **Fix**: Streaming memory leak — replaced `ArrayPool<byte>` with direct byte array allocation in `BufferedSegmentStream`. The shared pool retained large buffers indefinitely, causing memory to grow with each stream and never shrink. Also fixed undisposed segments not being drained on stream disposal.
*   **Fix**: Orphaned stream safety net — added 60-second idle timeout watchdog to `BufferedSegmentStream`. If no `ReadAsync` calls arrive for 60 seconds (indicating the HTTP response write is stuck due to a client disconnect not propagating through a reverse proxy), the stream self-cancels its workers and releases permits immediately.
*   **Fix**: Explicit cancellation in `NzbFileStream` disposal — `_streamCts.Cancel()` is now called before `_streamCts.Dispose()` in both sync and async disposal paths, ensuring cancellation notification reaches dependent code even if the normal disposal chain stalls.

## v0.6.23 (2026-04-09)
*   **Feature**: New `QueueAnalysis` connection type with its own capped semaphore (default: half of queue connections, min 2). Isolates the file-size analysis phase from regular queue processing and streaming, preventing analysis of bad NZBs from saturating the connection pool. Configurable via `QUEUE_ANALYSIS_MAX_CONNECTIONS` env var.
*   **Fix**: DMCA/takedown fast-fail in `AnalyzeNzbAsync`. When Smart Analysis detects article-not-found errors, a confirmation check probes a mid-NZB segment before committing to a full scan. If confirmed missing, the item fails immediately instead of scanning hundreds of dead segments. Prevents multi-minute connection pool burns on DMCA'd content.

## v0.6.22 (2026-04-08)
*   **Upstream Sync**: Adopted changes from upstream v0.6.2 and v0.6.3 releases.
*   **Fix**: WebDAV range requests past content boundary now return HTTP 416 (Range Not Satisfiable) instead of 500. Also fixed extraneous space in `Content-Range` header.
*   **Fix**: Centralized content-type resolution into `ContentTypeUtil` with FLAC (`audio/flac`) mapping — FLAC files served via WebDAV and `/view` now get the correct MIME type.
*   **Fix**: Frontend authentication now enforced via Express middleware instead of per-route loader checks. Prevents unauthenticated access to routes that bypass the root loader (e.g. direct `.data` requests).
*   **Fix**: "Delete mounted files" checkbox no longer shown when clearing a failed history item (no mounted files to delete). Failed items now show their error message in the confirmation dialog.
*   **Feature**: `/nzbs` WebDAV directory is now organized by category subdirectories (e.g. `/nzbs/tv/`, `/nzbs/movies/`). Categories are derived from configured categories and any categories present in the queue.
*   **Performance**: QueueItems unique index changed from `(FileName)` to `(Category, FileName)`, allowing the same filename in different categories and improving category-filtered queries.
*   **UI**: Added horizontal padding to queue/history table cells.
*   **Maintenance**: Renamed `MimeType` property to `ContentType` in `AddFileRequest`/`AddUrlRequest` for consistency with upstream.

## v0.6.21 (2026-03-26)
- Fixed: Files with missing articles no longer loop forever in the health check queue. Arr import detection now immediately marks `IsImported=true` when detected (no longer waits for retention period). Added 24-hour timeout: if arr never imports a file (e.g. rejected due to corruption), repair proceeds after 24h instead of looping indefinitely.

## v0.6.20 (2026-03-26)
- Fixed: OrganizedLinksUtil no longer logs FK constraint errors on startup when symlinks reference DavItems that have been deleted. Missing DavItems are now silently skipped.

## v0.6.19 (2026-03-26)
- Added: Delete button in Dav Explore allows direct deletion of files and folders from the virtual filesystem. Directories are recursively deleted. Protected system directories cannot be deleted.

## v0.6.14 (2026-03-25)
- Fixed: "Delete mounted files" now correctly removes virtual files even when they were previously unlinked by Sonarr/Radarr automatic archive cleanup. Uses recursive directory tree deletion via DownloadDirId instead of relying solely on HistoryItemId matching.

## v0.6.10 (2026-03-25)
*   **Fix**: Resolved "DavItem cannot be tracked because another instance with the same key value" crash in queue processing. When multiple aggregators (Rar, File, SevenZip, MultipartMkv) share the same DbContext, deterministic GUID collisions could occur if deobfuscation resolved different NZB files to the same filename. Added `IsAlreadyTracked()` guard in `BaseAggregator` that checks the ChangeTracker before calling `.Add()`, skipping duplicates instead of crashing.

## v0.6.3 (2026-03-21)
*   **Fix**: Health check completions now appear in the Analysis History tab. Previously, the Health Check Queue processed items but never recorded results to the shared Analysis History, so they were invisible in the UI.
*   **Fix**: Suppressed noisy "No routes matched location" error logs caused by browser-generated requests for static assets (apple-touch-icon, favicon variants, robots.txt, etc.) that React Router's SSR handler was processing.

## v0.6.2 (2026-03-21)
*   **Fix**: "Total Downloaded ALL TIME" on the System Dashboard now correctly tracks cumulative bandwidth across all time. Previously, the value only reflected the last 30 days because the database maintenance service pruned older bandwidth samples. The fix accumulates pruned bytes into a persistent counter before deletion, so the all-time total is preserved across prune cycles.

## v0.6.1 (2026-03-20)
*   **Versioning**: Switched to independent versioning scheme. Minor version tracks upstream nzbdav-dev/nzbdav sync level (currently synced to v0.6.0); patch auto-increments per build. Previous changelog entries used the 0.1.x scheme.
*   **UI**: Updated GitHub link to point to dgherman/nzbdav2. Changelog footer link now links to this changelog.
*   **Docs**: Replaced README with developer-focused version documenting provenance, original features, upstream sync history, and architectural divergences.

## v0.1.29 (2026-01-14)
*   **Fix**: Corrected rclone VFS disk cache deletion to use the proper path structure. The cache now correctly mirrors WebDAV paths directly instead of using an incorrect nested directory structure.
*   **Fix**: Fixed byte position tracking for multipart/RAR files. The UI now displays accurate progress across the entire file instead of resetting for each archive part.
*   **UI**: Progress slider now uses byte-based position for multipart files, providing accurate visual feedback for files spanning multiple RAR volumes.
*   **UI**: Improved connection grouping in Stats page - entries with `.mkv` extension and without now properly merge together.
*   **Performance**: Removed verbose "SEGMENT STORED" debug logging to reduce log noise during streaming.

## v0.1.28 (2026-01-06)
*   **Feature**: Added a "Run Health Check" button to the File Details modal across the application (Health, Stats, Explore). This allows users to manually trigger an immediate, high-priority `HEAD` health check for any specific file to verify its availability on Usenet.
*   **Fix**: Resolved a critical deadlock in `GlobalOperationLimiter` where retrying failed operations (like transient Usenet errors) would recursively acquire new permits without releasing the existing ones, eventually exhausting the permit pool and hanging the application.
*   **Logic**: Smart Analysis is now exclusive to 'Analysis' operations (for seek acceleration). Health Checks (Routine & Urgent) will now consistently perform full segment-by-segment verification to ensure maximum reliability, bypassing Smart Analysis optimization.
*   **Behavior**: Items imported by Sonarr/Radarr are now moved to the **Archived** state (soft delete) instead of being hard deleted immediately. This allows users to view them in the "Archived" filter if needed. They will be permanently deleted after the configured history retention period. Note: Virtual files for archived items are **retained** in the filesystem until the retention period expires, ensuring availability for seeding or other uses.
*   **Dependency**: Added `ffmpeg` to the Docker image to support future video verification and advanced analysis features.
*   **Feature**: Implemented "Media Analysis" (ffprobe) as part of the analysis workflow. When "Analyze" is triggered for a file, it now performs both NZB segment analysis (for instant seeking) and deep media verification using `ffprobe`. The full media metadata (video/audio streams, codecs) is displayed in the File Details modal.
*   **Feature**: Added a "Repair" button to the File Details modal. This allows users to manually trigger a repair process (which deletes the file from NzbDav and triggers a re-search in Sonarr/Radarr) directly from the file view.
*   **Stats**: Enhanced the "Mapped Files" table in the System Monitor to display media codec information (Video/Audio) extracted from analysis. Added support for searching/filtering mapped files by codec (e.g., "hevc", "dts").
*   **Stats**: Added filters to the "Mapped Files" table to show only analyzed files (`Analyzed Only`) or files missing video tracks (`Missing Video`), making it easier to identify problematic files.
*   **Logic**: Health Checks (Routine & Urgent) now automatically trigger "Media Analysis" (ffprobe) if the file is missing media metadata. This ensures that all files are eventually verified for stream integrity during the background maintenance cycle.
*   **Fix**: Resolved a critical infinite loop in `MultipartFileStream` that occurred when a part stream returned 0 bytes (EOF) prematurely due to metadata size mismatches (common in some obfuscated or 7z archives). The stream now correctly advances to the next part boundary.

## v0.1.27 (2026-01-05)
*   **Performance**: Implemented "Smart Analysis" for media files. The system now intelligently detects uniform segment sizes by checking only the first, second, and last segments of a file. If confirmed uniform, it skips the full segment-by-segment analysis, reducing the number of required network requests from thousands (O(N)) to just three (O(1)) for standard releases. This dramatically speeds up the "Analyzing..." phase for new content.

## v0.1.26 (2026-01-04)
*   **Fix**: Resolved a critical issue where the download queue could become permanently stuck ("looping") during high system load (e.g., streaming or intensive Sonarr scanning). This was caused by connection pool starvation where queue tasks were deprioritized and unable to acquire connections. Queue tasks now compete fairly for resources.
*   **Fix**: Resolved an issue where processing 7z archives could hang indefinitely if the file structure required extensive seeking or was corrupted. Added a timeout and improved logging for 7z header processing to fail fast and prevent queue stalling.

## v0.1.25 (2025-12-25)
*   **Optimization**: Implemented **Persistent Seek Cache**. Segment sizes and offsets are now cached in the database (via `DavNzbFiles` table), enabling instant O(log N) seeking and significantly reducing NNTP overhead for video playback.
*   **Maintenance**: Added `POST /api/maintenance/analyze/{id}` endpoint to manually populate the seek cache for specific NZB files.
*   **Reliability**: Automated cache population during routine and urgent `HEAD` health checks.

## v0.1.24 (2025-12-17)
*   **Logging**: Fixed a build error by ensuring `global::Usenet.Exceptions.NntpException` is correctly referenced in `ThreadSafeNntpClient.GetSegmentStreamAsync` to suppress stack traces for 'Received invalid response' errors.

## v0.1.23 (2025-12-17)
*   **Logging**: Fixed excessive error logging of `Usenet.Exceptions.NntpException` in `ThreadSafeNntpClient.GetSegmentStreamAsync`. This ensures that "Received invalid response" errors (often due to provider issues) are rethrown without generating full stack traces in the low-level client logs.

## v0.1.22 (2025-12-17)
*   **Tooling**: Enhanced `ArrHistoryTester` with verbose logging, including full JSON dumps of history events and debug-level traces of API calls, to aid in troubleshooting integration issues.
*   **Fix**: Corrected Sonarr history API calls to use the valid `seriesId` (singular) parameter instead of `seriesIds`.
*   **Fix**: Updated Sonarr and Radarr history lookups to request a larger `pageSize` (default 1000), ensuring grab events are found even in extensive history.

## v0.1.21 (2025-12-17)
*   **Fix**: Resolved a race condition where a routine health check could overwrite and clear the "Urgent" status of a file that failed streaming, effectively ignoring the failure. The system now re-verifies the file's status before saving results.
*   **Fix**: Corrected timeout handling for urgent health checks. If an urgent check times out, it is no longer rescheduled to the future (downgrading it to a routine check), but instead maintains its high priority to ensure a rigorous retry.

## v0.1.20 (2025-12-17)
*   **Optimization**: Implemented a smart health check strategy. Routine scheduled health checks now use the faster `STAT` command, while urgent health checks triggered by streaming failures continue to use the more reliable `HEAD` command. This balances performance for maintenance with accuracy for repair.

## v0.1.19 (2025-12-17)
*   **Fix**: Corrected Sonarr history API calls to use the valid `seriesId` (singular) parameter instead of `seriesIds`.
*   **Fix**: Updated Sonarr and Radarr history lookups to request a larger `pageSize` (default 1000), ensuring grab events are found even in extensive history.
*   **Tooling**: Added a new CLI tool (`--test-arr-history`) to verify Sonarr/Radarr history retrieval and blacklisting functionality directly from the container.

## v0.1.18 (2025-12-17)
*   **Reliability**: Enhanced Health Check accuracy by switching from `Stat` (STAT) to `GetArticleHeaders` (HEAD) for verifying article existence. This eliminates false positives where providers might report an article as existing (STAT OK) even when the body is missing or corrupted, ensuring that only truly available files pass the health check.

## v0.1.17 (2025-12-17)
*   **Logging**: Removed `UsenetArticleNotFoundException` error logs from the internal worker loop in `BufferedSegmentStream`. These errors are expected when searching for missing articles across providers and were causing unnecessary noise before the final result was determined.

## v0.1.16 (2025-12-17)
*   **Logging**: Fixed excessive error logging of `System.TimeoutException` in `ThreadSafeNntpClient.GetSegmentStreamAsync`. This ensures that expected timeout errors (often transient) are rethrown to higher layers for handling without generating full stack traces in the low-level client logs.

## v0.1.15 (2025-12-17)
*   **Logging**: Suppressed stack traces for `TimeoutException` in `BufferedSegmentStream`. This further reduces log noise for connection timeouts that bubble up from the low-level client, logging them as warnings instead of errors with full stack traces.

## v0.1.14 (2025-12-17)
*   **Logging**: Suppressed stack traces for `System.IO.IOException` (e.g., "Connection aborted") in `ThreadSafeNntpClient`. This reduces log noise for expected transient network issues during streaming.

## v0.1.13 (2025-12-17)
*   **Logging**: Fixed excessive error logging by ensuring `UsenetArticleNotFoundException` is rethrown without logging in the low-level `ThreadSafeNntpClient`. This prevents stack traces from appearing in the logs for every missing article when expected failures are handled by the multi-provider client.

## v0.1.12 (2025-12-17)
*   **Logging**: Added explicit log messages when a `UsenetArticleNotFoundException` triggers an immediate health check for a `DavItem`. This provides clearer visibility into the system's proactive repair actions.

## v0.1.11 (2025-12-17)
*   **Logging**: Enhanced `UsenetArticleNotFoundException` log messages to include the relevant Job Name/Nzb Name. This provides better context for identifying which specific content is missing articles, across both API requests and streaming operations.

## v0.1.10 (2025-12-17)
*   **Logging**: Suppressed stack traces for `UsenetArticleNotFoundException` in `BufferedSegmentStream`. This reduces log noise when all providers fail to find an article during streaming, while still logging the error message.

## v0.1.9 (2025-12-17)
*   **Maintenance**: Improved automatic cleanup of orphaned "Missing Article" logs. The system now periodically scans for and removes error logs associated with files that have been deleted or have invalid (Empty) Dav IDs, addressing the issue of persistent "00000000..." entries in the Missing Articles table.

## v0.1.8 (2025-12-17)
*   **Performance**: Significantly improved loading performance of the 'Deleted Files' table by adding a database index on `HealthCheckResult` (`RepairStatus`, `CreatedAt`).

## v0.1.7 (2025-12-17)
*   **Logging**: Suppressed stack traces for generic exceptions within `GetSegmentStreamAsync` in `ThreadSafeNntpClient` when the stack trace matches a specific pattern, to reduce log noise for internal client errors.

## v0.1.6 (2025-12-17)
*   **Logging**: Suppressed stack traces for `System.TimeoutException` originating from Usenet provider timeouts to reduce log noise.

## v0.1.5 (2025-12-16)
*   **UI Improvements**: Renamed "Job Name" column to "Scene Name" in the Mapped Files table for clarity.
*   **Performance**: The Mapped Files table now uses a persistent database table (`LocalLinks`), eliminating "Initializing" delays and high memory usage for large libraries. Deletions are automatically synchronized.
*   **Accuracy**: The Missing Articles table now dynamically checks import status against the persistent mapped files table, ensuring the "Imported" badge is always up-to-date.
*   **Log Management**: Added "Delete" button to the Missing Articles table to remove individual file entries from the log.
*   **Fix**: Resolved a `NullReferenceException` in `ArrClient` when attempting to mark history items as failed if the grab event could not be found in history.
*   **Media Management**: Added "Repair" button to the Mapped Files table, allowing manual triggering of file repair (delete & blacklist/search) directly from the mapping view.
*   **Testing**: Added "Manual Repair / Blacklist" tool in Settings > Radarr/Sonarr to test blacklisting logic by manually triggering repair for a release name.
*   **Robustness**: Enhanced repair logic (Health Check & Manual) to better handle Docker volume mapping discrepancies. It now attempts to find media items by filename or folder name if exact path matching fails.
*   **UI Clarity**: Changed the "Imported" column in the Missing Articles table to "Mapped" with a badge indicator for better clarity on files used by Sonarr/Radarr.
*   **Diagnostics**: Added a "NzbDav Path" column to the Missing Articles table, displaying the internal path (`/mnt/remote/nzbdav/.ids/...`) for easier debugging and reference.
*   **UI Improvements**: Cells in the Missing Articles table (Job Name, Filename, NzbDav Path) are now expandable on click to view full text that doesn't fit the column width.
*   **Maintenance**: Added "Connection Management" tools in Settings > Maintenance to forcefully reset active connections by type (Queue, Health Check, Streaming), useful for clearing stalled items.
*   **Log Management**: Added "Orphaned (Empty ID)" filter to the Missing Articles table to easily find errors associated with deleted files. Also added "Delete Selected" functionality for bulk log cleanup.
*   **Filtering**: Added a "Not Mapped" filter to the Missing Articles table to show files not linked in Sonarr/Radarr, and a "Blocked Only" checkbox for quick access to critical errors.
*   **Bug Fix**: Corrected the internal path format in the Missing Articles table to correctly show the nested ID structure (e.g., `/.ids/1/2/3/4/5/uuid...`).
*   **Optimization**: Renamed `BackfillIsImportedStatusAsync` to `BackfillDavItemIdsAsync` and streamlined the startup backfill process to focus on populating missing DavItem IDs for mapped files logic.
*   **Diagnostics**: Enhanced logging in `ArrClient` (Sonarr/Radarr) to dump recent history records when a "grab event" cannot be found during the repair/blacklist process, aiding in troubleshooting matching issues.

## v0.1.4 (2025-12-08)
*   **Performance Optimization**: Addressed slow UI loading for stats pages by refactoring backend services to use asynchronous database queries and enabling SQLite WAL (Write-Ahead Logging) mode for improved concurrency.
*   **Deleted Files UI Improvements**: The "Deleted Files" table now identifies and displays the original NZB/Job name for files with obfuscated filenames, making it easier to track which content was removed.
*   **Log Noise Reduction**: Reduced excessive logging for missing articles in the backend.
