# FizzWhirl Fork Sync Analysis

- **Date:** 2026-06-09
- **Source Repository:** https://github.com/FizzWhirl/nzbdav2 (downstream fork of this repo)
- **Fork Repository:** https://github.com/dgherman/nzbdav2 (this repo)
- **Previous sync:** none — first sync from FizzWhirl. (Upstream nzbdav-dev syncs are tracked separately; last one `75adf75` in `docs/upstream-sync-2026-04-08.md`.)

> **Note:** Unlike the nzbdav-dev syncs, this sync flows *backwards* — FizzWhirl forked
> us at `9f450cf`, accumulated ~200 commits, and **manually ported our v0.8.0/v0.8.1
> work into their tree** (their `1669702` wired our `LegacyRarFileMigration`; their tree
> contains our plan doc `2026-05-25-rar-segment-size-and-legacy-cleanup.md`). Their HEAD
> at sync time: `0bec4ca`.

## Current State

| Branch | Latest Commit | Date |
|--------|---------------|------|
| Ours (origin/main, pre-sync) | `2e67875` fix(provider-stats) | 2026-05-29 |
| FizzWhirl (fizzwhirl/main) | `0bec4ca` fix: resolve unknown connections… | 2026-06 |

**Merge-base:** `9f450cf`. ~200 FizzWhirl commits reviewed; 28 selected for adoption, 25 landed (3 found redundant/superseded during pick).

---

## Changes Implemented

All cherry-picked with `-x` (each commit message carries `(cherry picked from commit <FizzWhirl hash>)`). Branch: `fizzwhirl-sync-2026-06-09`, 25 commits.

### 1. Memory/OOM hardening (`05766e4`)
ArrayPool-pooled segment buffers, OOM cooldown gate, bounded BufferToEndStream backpressure, UsenetSharp pipe threshold tightening.
**Adaptation:** kept OUR Dockerfile GC defaults (`DOTNET_GCServer=0`, 512MB heap limit) — theirs flipped to Server GC + 2GB; that's a deployment policy choice, overridable per host in compose.

### 2. Stream permit/lifecycle fixes (`e21c211`, `2b38867`, `034d6c2`, `04a624b`, `f6e7e0f`)
Tracked limiter leases, permits held until dispose, per-segment cloned provider contexts, entry-scoped shared stream context, bounded urgent segment races.
**Adaptation (`034d6c2`):** their hunk also carried `hadTransientFailure` tracking and multi-provider ArticleNotFound retry from *unpicked* commits — excluded; only the context-isolation rename (`_usageContext.DetailsObject` → `ct.GetContext<ConnectionUsageContext>().DetailsObject`) plus `ConnectionUsageDetails.Clone()` were taken.

### 3. Bounded archive range prefetch (`cd1a55f`) + RAR header cap (`72928ff`)
`requestedEndByte` plumbed through `DavMultipartFileStream` with per-part bounding (`GetPartRequestedEndByte`); RAR header extraction capped at 6 global / 2 per-part connections.
**Adaptation:** their preview/analysis-mode conditionals dropped (no such modes here); AES-encrypted archives still skip range bounding. Our v0.8.0 lazy segment-size precompute in `RarProcessor` now uses `MaxRarHeaderConnectionsPerPart` (old `RarHeaderConnections` const was removed by their commit). `DatabaseStoreRarFile.cs` delete/modify conflict resolved as deleted (we removed legacy RAR serving in v0.8.0).

### 4. WebSocket reconnect backoff (`c1d203f`)
`createWebsocketBackoff` + `getBrowserWebsocketUrl` in websocket-util, applied across queue/usenet/server reconnect paths.
**Adaptation:** dropped their `Form` react-bootstrap import (unused in our usenet.tsx).

### 5. Missing-articles fixes (`5bac1da`, `8a78f52`)
PAR2 files never flagged blocking; NFC filename normalization for summary grouping.
**Adaptation:** PAR2 guard applied to OUR batch-based blocking detection (their version sits on unpicked compact-bitset evidence persistence, `a2aaef6`).

### 6. rclone fixes (`294cfcc`, `e53d7f5`, `b614413`, `c55c074`)
Host header preserved in PROPFIND hrefs; `PropFindResponseCleanupMiddleware` strips 404 propstat blocks (rclone v1.74.0); ranged GETs hardened vs corrupt yEnc segments; `LimitedLengthStream` archive read hardening.

### 7. SABnzbd zero-limit fix (`d4da883`)
`limit=0` treated as unlimited in GetQueue/GetHistory.

### 8. Arr replacement searches (`71eb16d`, `7a17db7`, `138d341`, `a8e2f8e`)
New `ArrReplacementSearchService` (registered in Program.cs DI): on queue item failure or health-check deletion, marks the item failed in Radarr/Sonarr history and triggers a replacement search; deletes remaining Arr hardlinks.
**Adaptation:**
- `138d341`'s QueueItemProcessor hunk carried their entire "Step 5" media-analysis (ffprobe) pipeline — excluded wholesale; only the ctor wiring + `NotifyQueueItemFailedAsync` on failure path kept.
- `BackgroundTaskQueue` dependency (their unpicked supervisor infra) removed from HealthCheckService — replacement-search notify is awaited directly.
- `backend/Utils/JobNameUtil.cs` copied from their tree (needed by HealthCheckService notify path).

### 9. Connection stats / NNTP fixes (`9b37fb2`, `89b5994`, `8f8f206`, `8b56775`, `0bec4ca`)
Connection stalling + negative health index + async safety fixes; `effectiveContext` fallback so context-less acquisitions show a descriptive label; `ConnectionUsageType.Unknown` → `Unlabeled` rename; short-lived connections recorded in provider stats; provider error buffer flushed on Dispose; `GetFileSizeAsync` preserves the NNTP connection after yEnc header reads and aligns adaptive timeout minimums with per-provider limits.
**Adaptation:** two `ConnectionUsageType.Unknown` references in our divergent `MultiConnectionNntpClient` lines renamed manually.

---

## Found Already Present / Superseded (no commit)

| FizzWhirl Commit | Why dropped |
|------------------|-------------|
| `e7cef65` bound BufferedSegmentStream prefetch to Range end | Already fully in our tree (identical `ComputeRelativeEndSegmentIndex`, `RequestedRangeEnd` plumbing) — arrived via earlier cross-pollination with our v0.8.0 work |
| `03c8446` persist analyzed segment sizes | Redundant: our v0.8.0 computes `SegmentSizes` at import + lazy backfill at play; theirs hooks their Step-3 probe machinery we don't have |
| `c77db85` release pool slot for doomed returns | Superseded: `9b37fb2` (picked) carries a more refined version with a `wasActive` guard |

## Deliberately Skipped (themes, per user decision)

| Theme | FizzWhirl Commits | Reason |
|-------|-------------------|--------|
| Media analysis pipeline (ffprobe smart probes, dual-point decode, DMCA handling, Step 5 deletion) | `d9ead6e`, `cdcb714`, `994833a`, `e9fed63`, +~10 | Large feature; heavy ffprobe dependency; not adopted this round |
| Health check overhaul (quick checks, HEAD-scan fallback, debounce, DMCA repair, concurrency config) | ~25 commits | Big surface; revisit separately |
| ~~Prometheus `/metrics` + PoolMetricsCollector~~ | `4adaf66`, `5c6fb1f`, `491f2a0`, `6f79d3d` | **ADOPTED 2026-06-10** in v0.10.0 — see Addendum below |
| Graceful-degradation cap + Truncated badge + moov-at-end detection | `d7a2c6c` saga | They flip-flopped (revert/reapply); needs scrutiny |
| Preview player in explore modal | `51085ca` + 5 | Nice-to-have UI |
| Settings UI reorg + UI polish series | ~20 commits | Cosmetic churn |
| DB self-healing / v1 blobstore migrations | ~15 commits | For drifted/upstream-v1 databases; ours is clean |
| CI guardrails, FizzWhirl publish refs, docs reports, reverted work (ArrayPool WebDAV pump pooling, Live Metrics tab) | rest | Fork-specific or net-removed in their own tree |

## Re-evaluate If

| Feature | Condition |
|---------|-----------|
| ~~Prometheus `/metrics` (`4adaf66` et al.)~~ | ADOPTED 2026-06-10 (v0.10.0) — see Addendum |
| `BackgroundTaskQueue` supervisor (`fe06886`) | If we adopt their health-check overhaul, restore the queued (non-awaited) Arr notify in HealthCheckService |
| Multi-provider ArticleNotFound retry + `hadTransientFailure` (excluded from `034d6c2` region) | If multi-provider article retries are wanted; pairs with `a2aaef6` compact evidence persistence |
| Their analysis-mode throttles (`db5ddaf`, `03c8446`) | Only meaningful if the media-analysis pipeline is adopted |

## Architectural Differences

- FizzWhirl's tree contains our v0.8.0/v0.8.1 verbatim, so file-level diffs against them exclude our recent work — convenient for future syncs.
- Their fix commits sit *on top of* their media-analysis/preview/health-overhaul infra (merged early in their history), so cherry-picks routinely drag in `isAnalysisMode` / `PreviewMode` / `BackgroundTaskQueue` references that must be stripped.
- Every FizzWhirl commit bumps the `Program.cs` BUILD banner and their README changelog — these conflict on every pick and are always resolved `--ours`.

---

## Addendum 2026-06-10: Prometheus `/metrics` adopted (v0.10.0)

Cherry-picked `4adaf66` (metrics core), `5c6fb1f` (UseMetricServer-before-auth 401 fix), `491f2a0` (frontend session-auth proxy + optional `METRICS_REQUIRE_API_KEY` backend gate), `6f79d3d` (persisted frontend session key). Only the usual banner/README conflicts; their `docs/fork-vs-upstream-analysis` doc kept deleted.

Critical-read findings and adaptations (follow-up commit on top of the picks):

- **Dropped the `path` label** on `nzbdav_shared_stream_{hits,misses}_total` — every call site passed `""`, and real paths would be an unbounded-cardinality trap (Grafana Cloud bills per active series).
- **Wired `nzbdav_shared_stream_active_readers`** — declared but never set in their tree (constant 0, even at their HEAD). `SharedStreamEntry.ActiveReaders` now exposed; `SharedStreamManager.RefreshGauges()` sampled by `PoolMetricsCollector` every 5s.
- **Deduplicated circuit-breaker threshold** — their `IsCircuitBreakerTripped => failures > 5` duplicated the magic `5` from the retry loop; extracted `CircuitBreakerFailureThreshold` const.

Known limitations, accepted as-is:

- Seek histogram measures time *inside* `Seek()` only; the real cold-seek cost (NNTP refetch/rebuffer) happens lazily on the next `Read()`, so `kind="cold"` ≈ stream-teardown cost, not time-to-first-byte.
- Pool metrics registry is keyed by `PoolName` (host); two pools with the same host (dual accounts) would collide — one registration overwrites the other and disposal unregisters both. Distinct hosts today.
- Deployment decision: `METRICS_REQUIRE_API_KEY` left **unset** (LAN-open backend `/metrics` on the NAS); the frontend proxy is session-authenticated regardless.

Skipped intermediate frontend state: an earlier FizzWhirl commit briefly forwarded `/metrics` unauthenticated through the frontend proxy (for their later-reverted Live Metrics tab); we adopted the final, session-authenticated wiring from `491f2a0` directly.

---

## Pickup Point

Next FizzWhirl sync starts from their commit `0bec4ca` (their main as of 2026-06-09).
