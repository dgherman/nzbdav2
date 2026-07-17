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

## Tuning Streaming Memory

Streaming holds segment buffers in memory while it reads ahead of the player. Two settings decide how much, and the defaults are tuned for ~30 streaming connections against a 4 GiB heap limit. **If playback is fine, leave them alone.**

| Setting | Default | What it does |
| --- | --- | --- |
| `usenet.prefetch-window` | `0` (auto) | How many segments the fetchers may run ahead of the reader. Auto = `stream buffer + connections`, raised to a floor of **300**. |
| `usenet.max-concurrent-buffered-streams` | `8` | How many buffered streams can exist at once. Note this is **not** "number of videos" — a multipart/RAR playback needs a slot per active part, plus the player's head/tail probes. |

### The arithmetic

```
peak resident ≈ prefetch-window × 1 MB × concurrent streams
```

So the defaults imply roughly `300 MB` per active stream, and a worst case of `~2.4 GB` if all 8 slots are in use. Two concurrent videos is about `600 MB`.

### If you are memory-constrained

Lower `usenet.prefetch-window` (say to 150), or lower `usenet.max-concurrent-buffered-streams`, or raise the container's heap limit (`DOTNET_GCHeapHardLimit`) if the host has room. Prefer raising the heap limit if you can: the streaming working set is now **bounded by these settings rather than by file size**, so headroom actually covers it.

### Do not set the window too low

This is the one that bites. Too *large* a window costs memory you can tune away. Too *small* a window **breaks playback outright** — the reader starves, playback dies on NNTP body-read timeouts, and the retries trip your providers' circuit breakers. During development, an effective window of 90 at 30 connections made a second concurrent video unplayable; 300 ran three concurrent streams clean. If you lower this and streams start failing with `Invalid NNTP Response` and breaker trips, raise it back before suspecting your provider.

The window has to be this generous mainly because connection-acquire contention dominates fetch latency ([#18](https://github.com/dgherman/nzbdav2/issues/18)); if that improves, the floor can come down.

### Checking what you are actually running

Each stream logs its effective window:

```
[BufferedStream] PREFETCH WINDOW: 300 segments (10.0x of 30 connections, source=configured, bufferSegmentCount=60), Job=...
```

`source` is `configured` (from the setting), `floor` (the computed value was below the floor), or `computed`.

## Upstream Sync

nzbdav2 tracks [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) and periodically cherry-picks relevant upstream changes manually. Each sync documents which changes were adopted, which were skipped, and the rationale for each decision. Sync history is in [`docs/upstream-sync-*.md`](./docs/). The most recent file contains the last reviewed upstream commit and a table of all items evaluated.

## Changelog

## v0.11.6 (2026-07-17)
Confirms the v0.11.5 OOM fix on production running its *default* path, and instruments what that fix exposed underneath: the number of streams a single file open creates.

### Streaming

*   **Logging (a stream's memory cost can now be read from the log instead of inferred)**: `[BufferedStream] PREFETCH WINDOW` reported the window but not the effective segment count, and the two differ whenever a client sends a bounded HTTP Range — the producer stops at the range end, so such a stream never costs a full window. Reading the window alone overstates a ranged read by an order of magnitude. The line now carries `holds<=NMB`, which bound applied (`window` or `segments`), and the effective/total segment counts.
*   **Logging (the startup banner quoted the per-stream log's own text, so every `grep` for that text also matched the banner)**: this inflated a production stream count from 15 to 17 and had already corrupted an earlier window verification. The banner no longer quotes log strings it wants operators to search for.

### Notes

*   **v0.11.5 verified on production ([#19](https://github.com/dgherman/nzbdav2/issues/19))**: previous validations all ran with `NZBDAV_PREFETCH_WINDOW` set, exercising the `configured` branch; the shipped default (`source=floor`, 300 segments) had never run under load. It now has, on the same 6924-segment file that reliably failed on v0.11.4: **0 fetch-path memory failures, 0 fetch errors, 0 timeouts, 0 breaker trips** across two concurrent videos. Peak LOH reached 4.19 GB against the 4 GiB heap limit and the GC held the line with ~31 blocking gen2 collections in a minute — the fix converts an out-of-memory failure into GC pressure, which is a better failure mode with no headroom left.
*   **The remaining constraint is stream count, not window size ([#18](https://github.com/dgherman/nzbdav2/issues/18))**: opening a single movie created **8 streams in 5 seconds**, saturating every `usenet.max-concurrent-buffered-streams` slot, each parking its own 300-segment window — which is exactly the measured 2.4 GB. The window fix is per-stream; the multiplier is #18.

## v0.11.5 (2026-07-16)
Fixes the streaming OOM ([#19](https://github.com/dgherman/nzbdav2/issues/19)) and the allocation bursts filed as [#22](https://github.com/dgherman/nzbdav2/issues/22), which turned out to be the same bug. Segment prefetch had no backpressure, so memory scaled with file size instead of with the configured buffer — but bounding it alone breaks playback, so the window also carries a floor.

### Streaming

*   **Fix (the backend died with `Internal CLR error (0x80131506)` during large-file playback)**: fetch workers pulled segments as fast as the providers would serve them and parked each completed ~1MB pooled buffer in `segmentSlots`, an array sized to the **whole file**. The only bounded queue, `_bufferChannel`, sits *downstream* of those slots and throttles the ordering task, not the fetchers — so nothing tied the fetch rate to the read rate and the workers raced to the end of the file. The resident set was therefore a function of file size, not of `bufferSegmentCount`. Caught on production: a **6924-segment file demanded ~6.9 GB** and hit the 4 GiB heap limit around segment 4870, with 15 fetch-path memory failures inside 2 seconds. The producer now holds a segment back until the reader is within the prefetch window of needing it; the index the reader is waiting on is always inside the window, so it can never be gated.
*   **Fix (bounding the window alone made a second concurrent video unplayable)**: too small a window starves the reader — playback dies on NNTP body-read timeouts and the retries trip the providers' breakers. An earlier build shipped an effective window of **90 for 30 connections** and the second stream would not play at all (14 corruption detections, 21 `Invalid NNTP Response`, 58 breaker trips in 8 minutes); it was reverted. The window is now floored at **300 segments**. Verified on production hardware at 3 concurrent streams: **0 corruption, 0 breaker trips, 0 fetch errors, 0 fetch-path memory failures**, heap flat at ~750 MB against 153 MB/s allocation and a 1.15 GB → 4 GiB cycle before. The floor is generous on purpose — too large costs memory an operator can tune away, too small breaks playback. It is this large mainly because connection-acquire contention dominates fetch latency ([#18](https://github.com/dgherman/nzbdav2/issues/18)).
*   **Fix (~1 GB/s allocation bursts with almost no network activity, [#22](https://github.com/dgherman/nzbdav2/issues/22))**: not a separate allocator. Each fetch worker that hit the memory limit forced a blocking, compacting Gen2 collection and backed off — deliberately not single-flighted — so a prefetch OOM storm became ~15 forced collections in 2 seconds, CPU at 342%, and a network that went quiet because the recovery path had suppressed it. No prefetch OOM, no storm: peak allocation is now **29 MB/s** with Gen2 collections back to roughly one per 45s.
*   **Feature — `usenet.prefetch-window`**: new setting (0 = auto) exposing the window. Costs about `window × 1 MB` per active stream; see [Tuning Streaming Memory](#tuning-streaming-memory). Each stream logs its effective window as `[BufferedStream] PREFETCH WINDOW`, with a `source` of `configured`, `floor` or `computed`, so a deployment can be tied to the window it actually ran.
*   **Tooling**: `SegmentBufferPoolDiagnostics` (set `NZBDAV_POOL_DIAG=1`) reports segment-buffer rents, returns, distinct arrays and the peak checked-out count in the harness. This is what settled #19: distinct arrays tracked the peak working set almost exactly (303 vs 299), which showed `ArrayPool` was reusing correctly and the fault was the size of the working set, not the pool.

Harness throughput numbers are not a useful comparison here: ~99% of fetch time there is connection-pool acquire ([#18](https://github.com/dgherman/nzbdav2/issues/18)), the mock provider's circuit breaker trips ~28 times per run on **baseline** too, and run-to-run spread is 7.6–34.1 MB/s. The pre-fix baseline bought its throughput by fetching 1.43× the segments and holding the whole file in RAM — which is the bug. Pool contention (#18) is the real throughput limiter and is unaddressed here.

## v0.11.4 (2026-07-16)
Cuts the largest avoidable allocator on the streaming path ([#14](https://github.com/dgherman/nzbdav2/issues/14)), stops a stalled segment from failing outright on single-provider setups ([#16](https://github.com/dgherman/nzbdav2/issues/16)), and repairs the benchmark harness that measures the first ([#15](https://github.com/dgherman/nzbdav2/issues/15)).

### Streaming

*   **Fix (a stalled segment failed outright when only one provider is configured)**: when a segment stalls, the straggler race records the slow provider and re-queues the segment excluding it, so the retry hedges on a *different* provider. `GetBalancedProviders` dropped excluded providers entirely, so with one provider configured the exclusion emptied the candidate list and the fetch failed with *"There are no usenet providers configured"* — false, and it points anyone reading the log at their settings. A transient stall escalated into 3 failed retries, a tripped circuit breaker, and the only provider skipped for up to 300s. Excluded providers are now offered as a last resort, after every preferred provider has actually errored — mirroring the tier `GetOrderedProviders` has always had, and which `GetBalancedProviders` had diverged from. Disabled providers are still never offered: that is a decision by the user, unlike exclusion, which is only a preference. The exhausted-selection error now also distinguishes "none configured" from "all filtered out".
*   **Performance (connection-pool stats built a UI message nobody was reading)**: `ConnectionPoolStats.OnEvent` serialized every active connection to JSON, computed a global and a per-provider usage breakdown, and formatted a message — on **every connection borrow and return**, from 11 raise sites in `ConnectionPool`, fanned out to every provider's pool by `UpdateUsageContext` on every buffer-starved read. It did all of this whether or not a websocket was attached, and during Plex playback none is: the message was built and dropped. Measured at **61,683 bytes per event** with 30 active connections — 120.5 KiB per segment, ~658 MiB over a 5,593-segment file. The event now updates its counters (integer work, always needed) and returns immediately when no subscriber is connected: **61,683 bytes → 0**. With the UI open the message is still built, but pushes are coalesced per provider to at most one per 250 ms, so a burst costs one message instead of thousands (3 bytes/event averaged over a burst).
*   **Performance**: `WebsocketManager.SendMessage` serialized the topic message and UTF-8 encoded it before discovering it had no sockets to send to. It now checks first. The last-message cache is still updated, so replay-on-connect is unaffected for every topic.
*   **Fix (stale connections panel on first page load)**: because a producer that skips publishing leaves its cached message stale, `WebsocketManager` now takes a state refresher that runs after a newly-connected subscriber replays cached state; `ConnectionPoolStats` republishes every provider over the top. This also corrects a pre-existing quirk — the cache holds one message per *topic*, so a multi-provider setup only ever replayed the last provider to change, and the rest of the panel stayed blank until each happened to fire.

End to end on the mock harness (300 MB, 30 connections, single provider, UI closed), averaged over 3 runs: allocation per MB read **3.63 → 3.13 MB**, ~96 MB less garbage per 200 MB delivered, or ~219 KB per segment. Extrapolated to a 5,593-segment file that is ~1.2 GB of garbage removed, and more on a multi-provider setup where the per-read fan-out multiplies. Streaming throughput was not the target and the run-to-run ranges overlap, though the spread narrowed (611–649 MB vs 676–775 MB).

### Tooling — `--test-full-nzb --mock-server` benchmark harness ([#15](https://github.com/dgherman/nzbdav2/issues/15))

The harness reported numbers that described nothing, so none of the above could be measured honestly until it was repaired.

*   **Tooling (mock server described every segment as byte 0)**: `MockNntpServer` served one hardcoded article for every message ID, so all segments reported `=ypart begin=1 end=<segmentSize>`. The client derives a segment's position from that header (`offset = begin - 1`), so every segment claimed offset 0 and `GetFileSizeAsync` — which reads the *last* segment's header — concluded the file was one segment long regardless of how many the NZB listed. A `--size=50` run reported `File size: 0.68 MB (74 segments)` and streamed 1 MB. The server now derives a per-segment `=ybegin`/`=ypart`/`=yend` from the generated NZB's real geometry, including the short trailing segment; the same run now reports `File size: 50.00 MB (74 segments)` and delivers 50 MB.
*   **Tooling (flat-file segments were unaddressable)**: the server recovered the segment index by regex over the message ID, but the pattern only matched the RAR form (`mock-000-000001-`) and never the flat-file form (`mock-flat-000001-`) that the default benchmark generates. Replaced with a layout registry built from `MockNzbGenerator`'s output, so segment identity no longer depends on parsing its name.
*   **Tooling (NNTP responses used LF, not CRLF)**: `StreamWriter` defaults to `Environment.NewLine`, which is a bare LF on Linux — where the benchmark runs. Responses are now explicitly CRLF-terminated. The `CAPABILITIES` reply also appended a terminator after its own `.`, leaving a stray blank line for the client to read as the next response.
*   **Tooling (benchmark ran with the database detached)**: `FullNzbTester` registered its `DbContext` via `AddDbContext<T>()`, whose DI overload injects an unconfigured options object — every provider-affinity query threw *"No database provider has been configured"* and was swallowed, so the harness benchmarked a code path with affinity silently disabled. It now constructs the context directly, using the app's real SQLite options.
*   **Tooling**: removed `stream.WriteTimeout = 1000` from the mock server. It was commented as a deadlock guard for clients that don't drain a body, but `NetworkStream` applies `WriteTimeout` only to synchronous `Write` — it has never had any effect on the `WriteAsync` calls it was guarding.
*   **Tooling**: mock server binds port 0 on request and exposes the assigned `Port`, and reuses one encoded payload per segment shape — it shares a process with the benchmark, so per-request payload garbage would land in the allocation counters the benchmark reports.

## v0.11.2 (2026-07-16)
Streaming stability and click-to-play latency pass over the shared-stream/buffered-stream path. Review findings and implementation plan in [`docs/plans/2026-07-16-streaming-stability-click-to-play-spec.md`](./docs/plans/2026-07-16-streaming-stability-click-to-play-spec.md).

*   **Fix (shared-stream pump thread leaked on every teardown)**: `SharedStreamEntry`'s pump parked on an untokened `ManualResetEventSlim.Wait()` whenever the last reader detached, and grace-period cleanup then disposed that gate out from under it. `Dispose` does not release waiters, so the pump thread stayed blocked **forever** — one leaked thread-pool thread per completed playback, showing up over long uptime as thread growth, memory creep and scheduling pressure. The gate wait now honours the entry's cancellation token, cleanup opens the gate before cancelling, and the gate/CTS are disposed only once the pump has actually exited (with a warning logged if it doesn't). Cleanup is also now strictly idempotent — a failed entry that was later disposed used to release its concurrent-stream slot twice (`SemaphoreFullException`).
*   **Fix (reader could hang at end-of-file)**: the pump signalled EOF and failure by swapping in a *fresh* `TaskCompletionSource` and completing the old one, then exiting — so a reader that checked `IsCompleted` a moment before the swap went on to await a signal that would never fire, hanging until its HTTP request timed out. Readers now capture the signal before re-checking their exit conditions, and terminal states leave the signal permanently completed. Client disconnects also propagate as cancellation instead of masquerading as a premature EOF (which made `NzbFileStream` build a replacement stream, and burn connections, for a client that had already gone).
*   **Fix (streaming-permit timeout wedged the stream)**: when a worker timed out waiting for a global streaming permit, its segment job was dropped on the floor — the in-order writer stalled at that index until the 60s idle watchdog tore the whole stream down and the player retried. Under multi-stream load this turned playback into 60–120s stall loops. The segment is now re-queued.
*   **Fix (connection-pool double release)**: after the stuck-connection sweeper reclaimed a long-held connection (releasing its pool permit), the borrower returning that connection with `Replace()` released a *second* time — throwing `SemaphoreFullException` out of `ConnectionLock.Dispose()` on the caller's path and driving the live-connection count negative, which inflates available-connection maths and lets the warm-floor top-up exceed a provider's connection cap. `Destroy` now mirrors the guard `Return` has had.
*   **Fix (graceful degradation aborted streams)**: the corrupted-segment list was a plain `List` written concurrently by fetch workers. A DMCA'd release fails many segments at once, so the list could corrupt or throw — aborting the stream exactly when zero-fill degradation was supposed to keep it playing. Now a `ConcurrentBag`.
*   **Performance (faster click-to-play on every file open)**: a player opening an `.mkv` seeks to the tail to read the Matroska Cues index. That position is correctly refused by the shared stream's front pump — but the manager then built an entire second entry anyway (starting a real fetch pipeline: permits, connections, live segment reads) only to lose the registration race against the entry it had just found, and tear it down synchronously (up to a 30s blocking dispose) before falling back. Every single open paid for a throwaway pipeline and duplicate Usenet reads. The manager now declines immediately and lets the caller use its private-stream fallback.
*   **Performance (cold-start responsiveness)**: the straggler race threshold sat at a flat 5s until ten fetches had completed — but those first segments *are* click-to-play. Bootstrap is now 2s (steady-state tuning is unchanged). The dynamic timeout is also no longer inflated by retry backoff sleeps, which previously slackened it right after a rough patch.
*   **Performance (warm connection pool)**: the 60s keepalive drained the idle stack and pinged connections serially before returning any, so a stream starting inside that window found nothing warm and paid a full TLS cold-start — the exact cost the warm floor exists to prevent. Connections are now returned to the stack one at a time as each ping completes. The borrow-time liveness check moved from 60s to 90s so warm connections no longer straddle the keepalive interval and pay a needless round-trip on the playback path.
*   **Fix (pausing a player forced a cold rebuild)**: a paused player stops consuming, the ring buffer fills and the pump stops reading — indistinguishable, to the buffered stream's read-only idle watchdog, from an orphaned stream. After 60s it self-cancelled the workers, so resuming paid a full connection ramp and buffer refill. The pump now marks the stream alive while it is paused on backpressure *with readers still attached*, for up to 10 minutes — bounded because "a reader is attached" is weaker evidence than it looks: the watchdog exists for clients that vanished without the server noticing (a disconnect that never propagated back through a reverse proxy), and such a reader stays attached forever. A real pause resumes well inside that window; a ghost gets reaped.
*   **Reliability (provider settings saves)**: replacing the usenet client on a config change disposed the old client immediately, while in-flight streams still held it — surfacing as `ObjectDisposedException` during playback. The replaced client now gets a 2-minute drain window.
*   **Fix (one segment's disposal could kill a whole stream)**: the segment fetch's `finally` disposed its stream unguarded. Under memory pressure the dispose chain itself allocates — the connection-pool stats event builds a string — so it threw `OutOfMemoryException` *from the finally*, which replaces the exception being handled and escapes the method entirely: past the OOM retry handler, past graceful degradation, into the worker's catch-all, which aborted the entire stream (`Critical worker error … Aborting stream`) over a single segment. Disposal failures are now swallowed and logged. Observed on a NAS streaming while a 10,426-segment NZB imported concurrently.
*   **Maintenance**: removed a dead stopwatch reset and a redundant `Array.Clear`, and added regression tests covering the pump leak, the EOF hang, the pool double-release, the wasted tail pipeline, the paused-player teardown and the throwing-dispose stream abort.

**Note on the forced GC under OOM pressure:** an earlier revision of this release single-flighted the blocking gen2 collect that `HandleOomPressure` runs on every `OutOfMemoryException`, on the theory that concurrent workers each triggering their own compacting collect was wasteful. It is wasteful, and it is also load-bearing: workers that skip the collect return immediately to renting ~1MB segment buffers against a heap that has not been compacted, which escalated a survivable OOM blip into a cascade that killed the runtime outright. Reverted — every OOM collects, as before.

## v0.11.1 (2026-07-06)
- **Fix**: Latency check no longer storms unreachable providers. A failing provider previously fired an unthrottled ping every 10s (the throttle only advanced on a *successful* ping) with no single-flight guard, which could wedge the backend on reboot cold-start — unresponsive `/health`, `unhealthy` container, and a restart loop. The per-provider latency check now runs at a fixed ~45s cadence with a single-flight guard (`LatencyCheckGate`).

## v0.11.0 (2026-06-24)
Three upstream cherry-picks (per-provider circuit breaker, SAB form-body params, `TZ` support) plus two fork-original streaming fixes for slow playback start. Upstream adoption analysis in [`docs/upstream-sync-2026-06-23.md`](./docs/upstream-sync-2026-06-23.md).

*   **Feature**: Skip failing usenet providers with a per-provider circuit breaker (upstream `c5fa860`). After 3 consecutive connection failures a provider trips into a 60s cooldown (doubling to a 5m cap; a single probe is allowed on expiry, resetting on success). Provider selection now stably deprioritizes tripped providers to the back so healthy providers are tried first, preventing one dead provider from stalling downloads.
*   **Fix**: SAB API now reads `cat`/`priority`/`pp` and query params from the form body as well as the query string (upstream `7059b10`), fixing category handling for form-posting clients like NZBDonkey. `GetFormParam` is guarded by `HasFormContentType` to avoid throwing on non-form posts.
*   **Feature**: Support the `TZ` environment variable for correct local timestamps by installing `tzdata` in the image (upstream `cfe0298`).
*   **Fix (endless "buffering" loop on playback start)**: `SharedStreamEntry.TryAttachReader` rejected positions behind the ring buffer or beyond EOF but had **no upper bound against the write frontier**, despite a code comment warning not to "attach too far ahead." A player opening an `.mkv` seeks to the file tail to read the Matroska Cues/SeekHead index before it can render; that tail read (hundreds of MB ahead of the front pump) attached to the **front** shared stream and blocked waiting for the in-order pump to crawl all the way to the last segment — which never happened in time. The reader stalled ~37s, the ordering task timed out, the stream tore down, and the player re-opened from zero, looping on "buffering" forever. Debug-traced: a tail seek to offset 825515305 (segment 1151/1152) logged `Attached to existing shared stream … Position=825036800` against a pump only at `NextIndexToWrite=157`. `TryAttachReader` now rejects positions more than one ring-buffer ahead of the write frontier, so the seek falls through to a dedicated `BufferedSegmentStream` at the seek target (the existing private-stream fallback) and the tail/Cues serve immediately.
*   **Fix (slow startup on the first play after an idle gap)**: Each provider's streaming connection pool now keeps a small **warm floor** hot at all times (`Math.Clamp(maxConnections / 6, 1, 8)`), and its idle timeout is raised from **120s to 15 minutes**. Debug-traced: when a play started after the pool had gone cold, it established ~30 fresh NNTP+TLS connections at once, starving the in-order segment writer; the first streaming request stalled long enough to be aborted (`[FTL] … application never completed`) and the player retried ~6s later against a now-warm pool — that retry gap was the bulk of the delay. The warm floor is pre-warmed on startup and kept alive by a 60s keepalive `DATE` ping so providers don't drop it, the idle sweeper never reaps below the floor, and the 15-minute idle window keeps a recent play's working set hot across inter-episode gaps. The gentler connection ramp also reduces per-provider circuit-breaker flapping. Warm connections hold no pool permit so they never block borrowers and stay within each provider's connection cap.

## v0.10.0 (2026-06-10)
Prometheus observability, adopted from the [FizzWhirl/nzbdav2](https://github.com/FizzWhirl/nzbdav2) fork (`4adaf66`, `5c6fb1f`, `491f2a0`, `6f79d3d`) with local adaptations.

*   **Feature**: Prometheus metrics endpoint at `/metrics`. Exposes shared-stream hits/misses (with miss `reason` label), active entries and active readers; per-pool live/idle/active/max connections, remaining semaphore slots, consecutive failures and circuit-breaker state (refreshed every 5s by a `PoolMetricsCollector` hosted service); a seek-latency histogram on `NzbFileStream.Seek` (`kind=cold|warm|noop|fresh`); plus the prometheus-net default .NET runtime/process metrics.
*   **Security**: The frontend `/metrics` proxy (port 3000) requires normal session authentication. The backend endpoint (port 8080) is served before the WebDAV auth middleware so scrapers don't get challenged; set `METRICS_REQUIRE_API_KEY=true` to require the internal API key (`x-api-key` header or `?apikey=`) for direct backend scrapes.
*   **Adaptation**: Removed the dead `path` label from the shared-stream counters (every call site passed `""`; real paths would be an unbounded-cardinality trap). Wired the `nzbdav_shared_stream_active_readers` gauge, which upstream declared but never set. Deduplicated the circuit-breaker failure threshold into a single constant.
*   **Reliability**: When `SESSION_KEY` is unset, the frontend persists its generated cookie-signing key under `/config/data-protection/frontend-session.key` so logins survive container restarts (`SESSION_KEY_FILE` overrides the path).

## v0.9.0 (2026-06-09)
Sync of 25 fixes from the [FizzWhirl/nzbdav2](https://github.com/FizzWhirl/nzbdav2) fork (a downstream fork of this repo). Full adoption analysis in [`docs/upstream-sync-2026-06-09-fizzwhirl.md`](./docs/upstream-sync-2026-06-09-fizzwhirl.md).

*   **Feature (Arr replacement searches)**: New `ArrReplacementSearchService` notifies Radarr/Sonarr when a queue item fails or when the health check deletes a dead file — the item is marked failed in Arr history and a replacement search is triggered automatically, instead of the Arr silently waiting on a file that no longer exists.
*   **Fix (memory/OOM hardening)**: Segment fetch buffers are pooled via `ArrayPool` (eliminating per-segment 1MB large-object-heap allocations), `OutOfMemoryException` during fetch now forces a GC and a global cooldown gate instead of cascading, and `BufferToEndStream` uses bounded pipe backpressure instead of unbounded buffering.
*   **Fix (stream permit lifecycle)**: Streaming connection permits use tracked disposable leases; global operation permits are held until the returned stream is disposed; urgent segment fetch races are bounded; shared stream context is entry-scoped; per-segment worker contexts are cloned so concurrent workers no longer overwrite each other's provider attribution/exclusion state.
*   **Perf (bounded archive prefetch)**: Ranged reads on RAR/7z multipart files now bound prefetch to the requested HTTP Range end (plus small overshoot) instead of prefetching to EOF — stops ~40MB of speculative Usenet reads per ranged request from rclone/ffprobe.
*   **Fix (RAR header extraction load)**: RAR header probing is capped by a global connection budget (6 connections, 2 per part) so large multipart imports no longer starve streaming.
*   **Fix (rclone compatibility)**: PROPFIND hrefs preserve the original Host header behind reverse proxies; 404 propstat blocks are stripped from PROPFIND responses (rclone v1.74.0 chokes on them); ranged GETs are hardened against corrupt yEnc segments and archive ranged reads against short reads.
*   **Fix (connection stats)**: Connections acquired without a usage context now fall back to a descriptive `Unlabeled` label (renamed from `Unknown`) instead of showing empty; health-check connections are labeled correctly; short-lived connections no longer vanish from provider stats; provider error buffer is flushed on shutdown; `GetFileSizeAsync` yEnc header reads no longer kill the NNTP connection and the adaptive timeout respects per-provider minimums.
*   **Fix (missing-articles stats)**: Missing PAR2 files are no longer flagged as blocking (nzbdav uses PAR2 only as a filename oracle, not for recovery); filenames are NFC-normalized when grouping summaries so unicode variants merge.
*   **Fix (SABnzbd API)**: Zero queue/history limits are treated as unlimited, matching SABnzbd semantics.
*   **Fix (frontend)**: WebSocket reconnects use exponential backoff instead of hammering the server; queue UI WebSocket parsing hardened against malformed frames.

## v0.8.1 (2026-05-29)
Provider Stats card fixes (reported in [#10](https://github.com/dgherman/nzbdav2/issues/10)).

*   **Fix (stats now record with affinity disabled)**: Per-provider usage stats were gated behind `provider-affinity.enable` in **two** places — both `RecordSuccess`/`RecordFailure` (in-memory counters) and `PersistStats` (the 5s timer that flushes them to the `NzbProviderStats` table). With affinity-based provider *selection* turned off, both no-op'd, so the table stayed empty and the Provider Stats card showed "No provider stats available" no matter how much was downloaded or streamed. Both recording and persistence are now decoupled from the affinity flag (only `GetPreferredProvider`, the selection logic, remains gated), so the card populates regardless of the selection setting.
*   **Change (moved to System Monitor)**: The Provider Stats card moved off the Queue page to **System Monitor → Statistics**, directly under Real-time Provider Status, alongside the other provider/bandwidth panels — no more scrolling past long queue/history lists to reach it. On its new home it now refreshes live with the tab's 2s revalidation; previously the component snapshotted its data with `useState` at mount and never updated until a full page reload.
*   **Fix (dark theme / visual consistency)**: The card's hardcoded light colors overrode the app's dark theme and looked out of place. It's now restyled to match the adjacent Real-time Provider Status panel exactly — same black translucent container, `bg="dark"` bordered provider cards in a 2-up grid, and the same Bootstrap metric typography (`fs-4 fw-bold` values, `text-muted small text-uppercase` labels, semantic colors) — dropping the bespoke CSS module entirely.

## v0.8.0 (2026-05-25)
Fixes RAR/7z (multipart) streaming, which could fail to play, stall on "loading", or serve 0 bytes (`Response Content-Length mismatch: too few bytes written (0 of …)`) — most visibly on RAR/7z-packed TV episodes that upstream played fine.

*   **Fix (exact seek offsets)**: Per-segment decoded sizes (`SegmentSizes`) are stored on `DavMultipartFile.FilePart` and passed to `NzbFileStream`, replacing the slow per-seek yEnc-header interpolation (and the encoded-vs-decoded size mismatch) that broke seeking on packed releases. Stored in the existing JSON column — **no database migration required**. Sizes are computed at import (RAR and 7z), with existing items self-healing on first play; an array that doesn't sum exactly to the part size is rejected, so wrong bytes are never served (worst case falls back to interpolation).
*   **Fix (no more stalls/0-bytes)**: The concurrent buffered-stream cap defaulted to **2** — far too low when a multipart file needs a slot per active part plus a player's parallel head/tail probes, causing `No semaphore slot available` and unbuffered fallback (slow). It now defaults to **8** (`usenet.max-concurrent-buffered-streams`), restoring parallel buffered throughput. And when a reader fell behind a shared stream's ring-buffer window, `NzbFileStream` looped re-attaching to the same lagging stream (`Premature EOF … recreating` forever); it now uses a private buffered stream on recreate, breaking the loop. `SetMaxConcurrentStreams` is floored at 1 so a 0/negative value can't crash startup (the v0.7.3 `SemaphoreSlim` class).
*   **Refactor (single streaming model)**: Legacy `DavRarFile` items are migrated to `DavMultipartFile` on startup (one-time, idempotent) and the legacy serving code removed, leaving one multipart streaming path. Pre-existing RAR items also gain the seek fix. The `DavRarFile` table/model is retained this release for the migration only and will be dropped in a follow-up.

## v0.7.4 (2026-05-22)
*   **Feature**: The User-Agent used to fetch NZB files over HTTP (SABnzbd `addurl` api) is now configurable, via the new **User Agent** field under Settings → SABnzbd, the `api.user-agent` config value, or the `NZB_GRAB_USER_AGENT` environment variable. Set a SABnzbd/NZBGet string for indexers that expect a known download client. The default remains a generic browser user-agent — unlike upstream's `nzbdav/<version>` default, this does not self-identify as a usenet streamer to indexers that are leery of them. The user-agent is now applied per-request rather than on the shared `HttpClient`, avoiding a header race under concurrent grabs. (Requested in [#9](https://github.com/dgherman/nzbdav2/issues/9).)

## v0.7.3 (2026-05-22)
*   **Fix**: Backend crashed on startup with `SemaphoreSlim` `maximumCount must be a positive number` when no usenet provider was configured (e.g. a fresh install). With 0 provider connections, `GlobalOperationLimiter` computed a low-priority gate size of 0 and threw before the config page could be served, leaving users unable to reach configuration. The connection pool size is now floored at 1 and the limiter floors total connections at 2, so the backend always boots and the config page is reachable. (Reported in [#8](https://github.com/dgherman/nzbdav2/issues/8).)
*   **Build**: Image version is now sourced from a single `VERSION` file at the repo root instead of a hardcoded `0.6.<run_number>` in CI. The Docker tag, changelog, and `Program.cs` BUILD string now stay in sync.

## v0.7.2 (2026-05-05)
*   **Fix**: `BufferedSegmentStream` prefetch is now bounded to the requested HTTP `Range` end byte (plus a 4-segment overshoot). Stremio, rclone vfs-cache, and ffprobe all issue closed `bytes=X-Y` range requests — previously each one triggered a full file prefetch (~40 MB of speculative Usenet reads), starving the connection pool and causing slow start times and timeouts. (Hat tip to [FizzWhirl](https://github.com/FizzWhirl/nzbdav2) for identifying and fixing this one.)
*   **Fix**: Connection pool slot was not released when a doomed connection was returned. Over time, each doomed connection permanently shrank the pool by one slot, causing progressive timeout worsening under normal provider turbulence.
*   **Fix**: Cancellation of streaming connections (`OperationCanceledException`) no longer increments the circuit-breaker failure counter or logs at Warning. Stremio cancels frequently on seeks and player stop — previously this tripped the 2-second circuit-breaker pause on every seek.

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
