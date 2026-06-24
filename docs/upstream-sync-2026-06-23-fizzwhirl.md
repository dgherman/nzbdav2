# FizzWhirl Fork Sync Analysis

- **Date:** 2026-06-23
- **Source Repository:** https://github.com/FizzWhirl/nzbdav2 (downstream fork of this repo)
- **Fork Repository:** https://github.com/dgherman/nzbdav2 (this repo)
- **Previous sync:** `0bec4ca` (2026-06-09) — see [`docs/upstream-sync-2026-06-09-fizzwhirl.md`](./upstream-sync-2026-06-09-fizzwhirl.md). (Canonical nzbdav-dev syncs are tracked separately; latest `794948b` in [`docs/upstream-sync-2026-06-23.md`](./upstream-sync-2026-06-23.md).)

## Current State

| Branch | Latest Commit | Date |
|--------|---------------|------|
| Ours (origin/main, pre-sync) | `a553312` docs(sync) | 2026-06-10 |
| FizzWhirl (fizzwhirl/main) | `4861644` Add upstream review and align metrics parity fixes | 2026-06-22 |

**New FizzWhirl commits since last sync:** 1 commit (`0bec4ca..4861644`).

---

## Changes Implemented

**None.** Nothing to adopt this round.

## Found Already Present / Superseded (no commit)

| FizzWhirl Commit | Why dropped |
|------------------|-------------|
| `4861644` "align metrics parity fixes" (their v0.6.87) | **This is FizzWhirl back-porting OUR own v0.10.0 metrics work into their tree.** Their three changes are exactly our 2026-06-10 Addendum decisions: (1) drop the high-cardinality `path` label from `nzbdav_shared_stream_{hits,misses}_total`; (2) wire `nzbdav_shared_stream_active_readers`; (3) deduplicate the circuit-breaker failure threshold into a constant. All three are already in our `main` (verified: `CircuitBreakerFailureThreshold` const in `ConnectionPool.cs`; only the `reason` label on the shared-stream counters in `AppMetrics.cs`; `SharedStreamActiveReaders` set in `SharedStreamManager.cs`). Their included `docs/upstream-review-2026-06-21.md` only read our README + sync log (not our actual source), so it wrongly concluded we hadn't applied the fixes and re-applied them. |

## Deliberately Skipped

None — the single new commit was already present (see above).

---

## Re-evaluate If

| Feature | Condition |
|---------|-----------|
| FizzWhirl active-readers wiring location | Theirs sets `active_readers` from `PoolMetricsCollector`; ours sets it from `SharedStreamManager`. Functionally identical — only revisit if the collector-based approach becomes preferable. |

(The deferred themes from the 2026-06-09 log — media-analysis/ffprobe pipeline, health-check overhaul, graceful-degradation/Truncated badge, preview player, DB self-healing — remain unchanged and un-adopted; FizzWhirl added no new work in those areas this round.)

---

## Architectural Differences

- The flow with FizzWhirl is bidirectional: they fork us, accumulate work, and periodically port our releases back into their tree. `4861644` is a pure back-port of our v0.10.0 metrics work, so it surfaced no new downstream changes.

---

## Pickup Point

Next FizzWhirl sync starts from their commit `4861644` (fizzwhirl/main as of 2026-06-23).
