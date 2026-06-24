# Upstream Sync Analysis

- **Date:** 2026-06-23
- **Upstream Repository:** https://github.com/nzbdav-dev/nzbdav (canonical upstream)
- **Fork Repository:** https://github.com/dgherman/nzbdav2
- **Previous sync:** `75adf75` (2026-04-08) — see [`docs/upstream-sync-2026-04-08.md`](./upstream-sync-2026-04-08.md). (FizzWhirl downstream syncs are tracked separately; latest `4861644` in [`docs/upstream-sync-2026-06-23-fizzwhirl.md`](./upstream-sync-2026-06-23-fizzwhirl.md).)

## Current State

| Branch | Latest Commit | Date |
|--------|---------------|------|
| Fork (origin/main, pre-sync) | `a553312` docs(sync): mark /metrics deployed | 2026-06-10 |
| Upstream (nzbdav-dev/main) | `794948b` fix(nntp): tag provider name in logs (#441) | 2026-06 |

**New upstream commits since last sync:** 19 commits (`75adf75..794948b`).

---

## Changes Implemented

Branch: `upstream-sync-2026-06-23`, 3 commits.

### 1. Skip failing usenet providers (circuit breaker) (from upstream `c5fa860`)

| Fork Commit | Description |
|-------------|-------------|
| `f9f6306` | New `ProviderCircuitBreaker`; wired into `MultiConnectionNntpClient`; tripped-provider deprioritization in selection |

- Added `backend/Clients/Usenet/Connections/ProviderCircuitBreaker.cs` verbatim from upstream (3 consecutive failures → 60s cooldown, doubling to a 5m cap; single probe allowed on expiry; resets on success).
- **Adaptation (ctor):** upstream threads the breaker through the `MultiConnectionNntpClient` ctor from `UsenetStreamingClient`. Our ctor is heavily diverged (optional named params for bandwidth/limiter/error services, latency timer). Instead of changing the ctor signature + DI wiring, each client **self-constructs** its breaker keyed by `_host`. This leaves `UsenetStreamingClient` untouched — the breaker is 1:1 with the client/provider anyway.
- **Adaptation (record points):** upstream has a single command loop; we have two (`RunWithConnection<T>` and `RunStreamWithConnection`). `RecordFailure()` placed in the retryable-exception catch of both; `RecordSuccess()` on the success path of both (mirroring upstream's catch/success placement).
- **Adaptation (selection):** upstream's `GetOrderedProviders` is the simple 5-line version and **drops** tripped providers (`healthy.Count > 0 ? healthy : enabled`). Ours is heavily diverged into two selectors (`GetOrderedProviders` with affinity/preferred/excluded plumbing, and `GetBalancedProviders` for buffered streaming). Rather than dropping, both selectors **stably push tripped providers to the back** via `.OrderBy(x => x.IsTripped)` after `.Distinct()`. This preserves the "always return at least one" guarantee and our affinity/exclusion ordering, while still trying healthy providers first and allowing a cooldown probe when all healthy providers are exhausted.

### 2. NZBDonkey form-category compatibility (from upstream `7059b10`)

| Fork Commit | Description |
|-------------|-------------|
| `a1c8c36` | `GetRequestParam`/`GetFormParam` helpers; SAB readers switched to `GetRequestParam` |

- Upstream's tree already had `GetRequestParam` (query ∪ form); the #316 commit only guards `GetFormParam` with `HasFormContentType`. Our fork had **diverged earlier and dropped both helpers**, using `GetQueryParam` everywhere — so form-posting SAB clients (NZBDonkey) couldn't set `cat`/`priority`/etc.
- **Adaptation:** restored `GetRequestParam` (`GetQueryParam(key) ?? GetFormParam(key)`) and the `HasFormContentType`-guarded `GetFormParam`, and wrapped `GetQueryParam` in `StringUtil.EmptyToNull` (matches upstream; fixes empty `cat=` bypassing the configured default). Switched the request-data readers to `GetRequestParam`: `AddFileRequest`, `AddUrlRequest`, `GetQueueRequest`, `GetHistoryRequest`, `RemoveFromHistoryRequest`, and the `queue`/`history` `name` routing in `SabApiController`. The `mode` discriminator stays query-only (matches upstream; NZBDonkey sends `mode` in the query string). Our fork-only `priority`/`requeue` name cases were converted too, for consistency.

### 3. TZ env var support (from upstream `cfe0298`)

| Fork Commit | Description |
|-------------|-------------|
| `fdca9bc` | Add `tzdata` to the runtime image apk packages |

- One-line Dockerfile change: append `tzdata` to the `apk add` line so the Alpine runtime honors the `TZ` env var (correct local timestamps in logs/UI). Our apk line diverged (adds `sqlite ffmpeg`) but the change applies cleanly.

---

## Deliberately Skipped

| Feature | Upstream Commits | Reason |
|---------|------------------|--------|
| Tag provider name in nntp lock/command-error logs | `794948b` | Low value; our `MultiConnectionNntpClient` already provider-tags via the FizzWhirl provider-stats work, and the touched hunks are diverged |
| Schedule the `RemoveOrphanedFiles` task (backend service + UI) | `ffcdcfd`, `807573b` | Our cleanup/health-check pipeline (`RemoveUnlinkedFilesTask`, `HealthCheckService`) diverged from upstream's orphan task; the scheduling UI is low value |
| README + GitHub issue template | `941bf74` | Non-functional, upstream-specific |
| Remove `vite-tsconfig-paths` plugin; `npm audit fix` | `c2bdf1d`, `a71cf69` | Our frontend build diverged; we manage our own deps |
| Release 0.6.4 chore | `c12a6ea` | Version bump only |
| Dependency bumps ×9 (vite 7→8, tailwind, react-router, react, dotnet group, dependabot grouping, `@types/node`, isbot, ws) | `2efc0c2`, `2f1c0f8`, `27d4cea`, `5ce46bc`, `aae1e43`, `96d866a`, `680e80d`, `b054f42`, `cb42d73` | Heavily diverged fork; upstream lockfiles don't apply and we manage our own deps |

---

## Re-evaluate If

| Feature | Condition |
|---------|-----------|
| `RemoveOrphanedFiles` scheduling (`ffcdcfd`, `807573b`) | If we align our cleanup task with upstream's orphan task and want a user-facing schedule for it |
| Provider-name log tagging (`794948b`) | If diagnosing provider attribution gaps in nntp lock/command-error logs |

---

## Architectural Differences

- **Two upstreams.** This fork tracks both the canonical `nzbdav-dev/nzbdav` (this log) and the downstream `FizzWhirl/nzbdav2` (separate `-fizzwhirl` logs). Most nzbdav-dev dependency/CI/docs churn is skipped; functional fixes are cherry-adapted.
- **NNTP client divergence.** Our `MultiConnectionNntpClient` ctor and the `MultiProviderNntpClient` provider selectors (`GetOrderedProviders`, `GetBalancedProviders`) are substantially rewritten (bandwidth service, affinity/epsilon-greedy exploration, per-job/per-segment exclusions, sticky `LastSuccessfulProviderContext`). Upstream changes to provider selection must be re-expressed against this structure, not pasted.
- **Param helpers.** Our `HttpContextExtensions` had been trimmed to query-only; upstream's query∪form `GetRequestParam` had to be reintroduced rather than diffed.

---

## Pickup Point

Next nzbdav-dev sync starts from upstream commit `794948b` (nzbdav-dev/main as of 2026-06-23).
