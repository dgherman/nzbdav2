# Upstream Sync — 2026-03-10

Comparison of upstream [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) releases
v0.5.38 and v0.6.0 against our fork [dgherman/nzbdav2](https://github.com/dgherman/nzbdav2).

Previous sync documented in `/UPSTREAM_ANALYSIS.md` (adopted: repair retry logic,
context propagation, RAR exception unwrapping).

**Sync completed: 2026-03-13**
**Last upstream commit reviewed:** all commits up to and including `v0.6.0` tag (2026-03-09)
**Our fork .NET version after sync:** .NET 10.0

---

## Upstream Releases Since Last Sync

| Release | Tag | Date | Type |
|---------|-----|------|------|
| 0.5.4 | v0.5.4 | 2025-12-10 | Feature (multiple providers, .strm imports, UsenetSharp switch) |
| 0.5.38 | v0.5.38 | 2026-03-09 | Feature + compat fixes |
| **0.6.0** | **v0.6.0** | **2026-03-09** | **Breaking release** (blobstore, compression, non-reversible) |

---

## Status Summary

| # | Item | Status | Branch |
|---|------|--------|--------|
| 1 | Zstd payload compression | DONE | `upstream/zstd-compression` (merged) |
| 2a | History-aware health check filtering | DONE | `upstream/history-health-checks` (merged) |
| 2b | History-aware cleanup (schema + services) | DONE | `upstream/history-cleanup` (merged) |
| 3 | Duplicate NZB segment fallback | DONE | `upstream/duplicate-segment-fallback` (merged) |
| 4 | `/content` recovery after restart | DONE | `upstream/content-recovery` (merged) |
| 5 | Blobstore migration | **SKIPPED** | `upstream/historyitem-compression` (merged — compression only) |
| 6a | SQLite VACUUM on startup | DONE | `upstream/quick-wins` (merged) |
| 6b | Archive passwords from NZB filenames | — | Already present in fork |
| 6c | Category-specific health checks | DONE | `upstream/quick-wins` (merged) |
| 6d | Export NZB from Dav Explore | **SKIPPED** | — |
| 6e | User-agent configuration | **SKIPPED** | — |
| 7 | Compatibility fixes (rclone, AddUrl, Arr log) | DONE | `upstream/dotnet10-upgrade` (merged) |
| 8 | .NET 10.0 upgrade | DONE | `upstream/dotnet10-upgrade` (merged) |
| 9 | Bug fixes (Mar 1-10 commits) | DONE | `upstream/bug-fixes` (merged) |
| 10 | Rclone vfs/forget integration | DONE | `upstream/bug-fixes` (merged) |
| 11 | Frontend UI improvements | DONE (11a skipped) | `upstream/bug-fixes` (merged) |

---

## What Was Deliberately Skipped (and Why)

These items were evaluated and intentionally not adopted. This section exists so the
next sync knows not to re-evaluate these unless circumstances change.

### 5. Blobstore Migration

**Upstream commits:** `e9f2464`, `eb9486c`, `fa9e637`, `d2cee00`, `1bd18f1`, `678afde`,
`114e570`, `b6c6258`, `345465f`, `5b2e949`, `26eabae`, `82fb5f9`, `9cc788d`, `6b9fd43`, `6b43e82`

**Decision:** Skipped. High-risk, non-reversible migration that moves NZB XML content
from SQLite rows to filesystem blobs. Instead adopted Zstd compression on HistoryItem
NzbContents via EF Core value converters, achieving 31% DB size reduction (856MB → 592MB
after VACUUM) without the risk.

**What this means for the fork:**
- We store NZB content in-DB with Zstd compression (not on filesystem)
- Upstream's `BlobStore.cs`, `NzbBlobCleanupService`, `DownloadNzb` controller all
  reference blobstore and are incompatible with our approach
- `DavItem.SubType` column was cherry-picked independently for item 2b (needed for
  HistoryItemId tracking), but we don't use the blobstore SubType enum values

**Re-evaluate if:** DB size becomes a problem again, or upstream makes blobstore reversible.

### 6d. Export NZB from Dav Explore

**Why skipped:** Depends on blobstore (`nzbBlobId`). Could be reimplemented to read from
our in-DB NzbContents if the feature is wanted later.

### 6e. User-Agent Configuration

**Why skipped:** NNTP protocol doesn't support user-agent headers. The setting has no effect.

### 11a. Explore Page Actions Dropdown

**Upstream commit:** `db33830`

**Why skipped:** Our explore page already has the `FileDetailsModal` with richer actions
(health check, repair, analyze, test download, provider stats) triggered on file click.
Upstream's dropdown only provides Preview/Download/Export NZB. The Download action is
now available via `?download=true` on the `/view` route (item 11b). Export NZB is
blobstore-dependent.

### 12. Deferred Features (from prior UPSTREAM_ANALYSIS.md)

| Feature | Upstream Commit | Why Deferred |
|---------|----------------|--------------|
| PrioritizedSemaphore (connection fairness) | `7af47c6` | Our `GlobalOperationLimiter` works; revisit if contention issues arise |
| 7z progress tracking (MultiProgress) | `20b69b0` | Adopt if 7z streaming becomes a priority |
| UnbufferedMultiSegmentStream | various | Potential fallback for low-memory; not needed currently |

---

## What Was Adopted (with Architectural Differences from Upstream)

These items were adopted but our implementation differs from upstream. Important
context for the next sync to avoid re-adopting upstream's approach.

### Rclone Integration (Item 10)

**Upstream approach:** Static `RcloneClient` class, flat config keys (`rclone.host`,
`rclone.user`, `rclone.pass`, `rclone.rc-enabled`).

**Our approach:** DI-injected `RcloneRcService` singleton with `IHttpClientFactory`,
single JSON config blob at `rclone.rc` key. Additional disk cache deletion feature
(`DeleteFromDiskCache`) that upstream doesn't have.

**vfs/forget integration:** We use a static `VfsForgetCallback` on `DavDatabaseContext`
wired to `RcloneRcService.ForgetAsync` at startup, plus explicit `TriggerVfsForget`
calls for bulk operations (HistoryCleanupService, RemoveUnlinkedFilesTask) that bypass
EF change tracking. Upstream wires directly from the static client.

### History-Aware Cleanup (Item 2b)

**Upstream approach:** Includes `DavCleanupItem` table + `DavCleanupService` (batched
directory deletion), removes parent-child FK cascade on DavItems.

**Our approach:** Adopted `HistoryCleanupItem` + `HistoryCleanupService` but skipped
`DavCleanupItem`/`DavCleanupService` and kept the cascade FK. Upstream needed to remove
cascade because blobstore triggers fire on cascade deletes; we don't have blobstore
triggers so cascade works fine and is simpler.

### SIGTERM Handling (Item 9d)

**Upstream approach:** Uses `SigtermUtil.IsSigtermTriggered()` with `when` filter on catch
blocks across 5+ services including `BlobCleanupService`, `NzbBlobCleanupService`.

**Our approach:** Added `IsSigtermTriggered()` to our existing `SigtermUtil`. Our services
already handle `OperationCanceledException` via `stoppingToken.IsCancellationRequested`
pattern. Added the guard to `DatabaseMaintenanceService` which was missing it.

---

## Pickup Point for Next Sync

**Last upstream commit reviewed:** All commits through 2026-03-09 (v0.6.0 release).
The specific commit hashes reviewed are listed in the Reference section below.

**To start the next sync:**
1. Run `git log v0.6.0..HEAD --oneline` in the upstream repo to see new commits
2. Filter out Dependabot/dependency bumps
3. Check this doc's "Deliberately Skipped" section before re-evaluating those areas
4. Check "Architectural Differences" section to avoid overwriting our patterns

---

## Reference: All Upstream Commits Reviewed

| Commit | Description | Item |
|--------|-------------|------|
| PR#199 | Database optimization (compression, retention, maintenance) | 1, 6a |
| PR#215 | Archive passwords from NZB filenames | 6b |
| PR#248 | Kodi scrubbing fixes | 7 |
| PR#265 | Save disabled providers without testing | 6 (minor) |
| PR#271 | Infuse WebDAV compatibility | 7 |
| PR#310 | Duplicate NZB segment fallback | 3 |
| PR#311 | `/content` recovery after restart | 4 |
| `e9f2464`–`6b43e82` | Blobstore migration (15 commits) | 5 (SKIPPED) |
| `1409a75`–`12c5cec` | History-aware cleanup (5 commits) | 2b |
| `053a596` | Duplicate NZB files processing fix | 9a |
| `c37be6f` | URL-encoded request proxying | 9b |
| `df8b845` | Special chars in filename passwords | 9c |
| `b5c8a7d` | Suppress TaskCanceledException on SIGTERM | 9d |
| `3155158`–`86c631f` | Rclone vfs/forget integration (9 commits) | 10 |
| `db33830`–`3a97ee0` | Frontend UI improvements (5 commits) | 11 |
| `7af47c6` | PrioritizedSemaphore | 12 (DEFERRED) |
| `20b69b0` | 7z MultiProgress | 12 (DEFERRED) |
