# Latency-check storm fix — design

**Date:** 2026-07-06
**Component:** `backend/Clients/Usenet/MultiConnectionNntpClient.cs`
**Type:** bugfix (concurrency / resilience)

## Problem

On the NAS (2026-07-06), the `nzbdav2` backend wedged: `/health` stopped responding (curl got 0 bytes, timed out at the 10s healthcheck timeout), the container went `unhealthy`, and the process sat with **idle CPU and flat memory** — a stall/deadlock, not a hot loop or OOM. The entrypoint supervisor (`/entrypoint.sh` runs backend + frontend; `wait_either` exits when either child exits, `exit 0`) then let `restart: unless-stopped` restart it, producing a ~3.5-minute restart loop, until it finally hung un-restarted (both children alive, backend deadlocked). It first appeared after a DSM upgrade + reboot the night before (cold start).

## Root cause

`MultiConnectionNntpClient.CheckLatency` is a `Timer` callback firing **every 10s per provider** (`MultiConnectionNntpClient.cs:71`). Its only throttle is:

```csharp
if (DateTimeOffset.UtcNow - _lastLatencyRecordTime <= TimeSpan.FromSeconds(45)) return;
```

`_lastLatencyRecordTime` is advanced **only on a successful ping** (`RunWithConnection`, line 250 / the stream variant). When a provider is unreachable — exactly a reboot cold-start — pings fail, the timestamp never advances, and there is **no single-flight guard**. So the timer fires an unthrottled `Task.Run` ping every 10s per provider. Across ~8 providers this is a continuous storm of acquire/cancel/release churn on the connection pool + custom semaphores (`GlobalOperationLimiter` → `PrioritizedSemaphore` / `ExtendedSemaphoreSlim`).

The usenet layer is async all the way down (no `.Result`/`.Wait()`/`.GetAwaiter().GetResult()`/`Thread.Sleep` in `Clients/Usenet`), so this is **not** thread-pool starvation from blocking sockets. The storm is the *trigger*; it is strongly suspected to expose a lost-wakeup / release-accounting race in the custom semaphores that leaves permits unreleased and every request awaiting forever (idle CPU + total HTTP hang, `/health` included since it is `app.MapHealthChecks("/health")` on the Kestrel pipeline).

## Scope

Fix the **confirmed trigger** — the unthrottled latency-check storm. This resolves the observed incident with high confidence because, without the storm, the suspected race is not hit.

**Non-goals (explicit):**
- Do **not** modify `PrioritizedSemaphore` / `ExtendedSemaphoreSlim` / `GlobalOperationLimiter`. The suspected lost-wakeup race is a separate, repro-driven task — blind-patching a concurrency primitive is out of scope.
- No permit-acquisition timeout in this change.
- Do not change the 10s `Timer` interval (the gate governs effective cadence).

## Design

Backoff behavior chosen: **fixed ~45s cadence + single-flight** (lowest-risk, matches current healthy cadence, kills the storm).

Extract the fire decision into a pure, testable unit and add a single-flight guard.

New fields on `MultiConnectionNntpClient`:
```csharp
private DateTimeOffset _lastLatencyAttemptTime = DateTimeOffset.MinValue;
private int _latencyCheckInFlight; // 0 = idle, 1 = running; Interlocked-guarded
```

Decision + release methods (`internal` for test visibility):
```csharp
internal bool TryBeginLatencyCheck(DateTimeOffset now)
{
    // Throttle every ATTEMPT (success OR failure) to the ~45s cadence.
    if (now - _lastLatencyAttemptTime <= TimeSpan.FromSeconds(45)) return false;
    // Single-flight: only one ping in flight at a time.
    if (Interlocked.CompareExchange(ref _latencyCheckInFlight, 1, 0) != 0) return false;
    _lastLatencyAttemptTime = now;
    return true;
}

internal void EndLatencyCheck() => Interlocked.Exchange(ref _latencyCheckInFlight, 0);
```

`CheckLatency` becomes:
```csharp
private void CheckLatency(object? state)
{
    if (!TryBeginLatencyCheck(DateTimeOffset.UtcNow)) return;
    Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var _ = cts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Analysis, "Latency Check"));
            await DateAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Debug("Latency check (ping) failed for provider {Host}: {Error}", _host, ex.Message);
        }
        finally
        {
            EndLatencyCheck(); // release single-flight even if the ping threw
        }
    });
}
```

Notes:
- The old `_lastLatencyRecordTime` field is **kept** (still recorded on successful operations for latency metrics); only the *gate* moves to `_lastLatencyAttemptTime`. Implementation must confirm no other reader depends on `_lastLatencyRecordTime` for throttling semantics.
- The attempt timestamp is set **inside** `TryBeginLatencyCheck`, after both gates pass, so cadence counts from the start of an attempt.
- `EndLatencyCheck` in a `finally` guarantees the single-flight flag is released on any exit path.

## Data flow

`Timer (10s)` → `CheckLatency` → `TryBeginLatencyCheck(now)`:
- returns `false` if within 45s of last attempt (throttle) or a ping is already in flight (single-flight) → callback returns, nothing fired.
- returns `true` → records attempt time, sets in-flight, fires one `Task.Run` ping (10s cts) → `finally` calls `EndLatencyCheck`.

Healthy providers: unchanged behavior (~45s cadence, successful pings). Failing providers: back off to the same ~45s cadence instead of 10s; never overlap.

## Error handling

- Ping exceptions are caught and logged at Debug (unchanged).
- `EndLatencyCheck` in `finally` ensures the single-flight flag never leaks, even on exception or cancellation.
- No new failure modes: `TryBeginLatencyCheck` is non-blocking and allocation-free.

## Testing (TDD)

Unit tests on `TryBeginLatencyCheck` / `EndLatencyCheck` with injected `now` — deterministic, no timers/sockets/semaphores:

1. First call at `t0` → `true` (fires); attempt time recorded.
2. Second call at `t0` without `EndLatencyCheck` → `false` (single-flight).
3. After `EndLatencyCheck`, call at `t0 + 20s` → `false` (throttle < 45s).
4. After `EndLatencyCheck`, call at `t0 + 46s` → `true` (throttle elapsed).
5. `EndLatencyCheck` releases the flag so a subsequent (post-throttle) call can begin — i.e. the flag does not leak across a completed check.

Success criteria: at most one latency check in flight per client; attempts no more frequent than ~45s regardless of success/failure; healthy-path cadence unchanged.

## Deploy (fork pipeline, per ARR.md)

1. Bump `VERSION` (patch: `0.11.0` → `0.11.1`).
2. Add `README.md` changelog entry `## v0.11.1 (2026-07-06)`.
3. Update `backend/Program.cs` `BUILD v...` string.
4. Push to a test branch → CI builds image → verify (backend boots healthy, no latency-check storm under a simulated dead provider) → merge to master → pull + restart on NAS.

## References

- Prior related spec: `docs/superpowers/specs/2026-04-08-connection-pool-resilience-design.md`.
- Memory: `project_nzbdav2_backend_deadlock`, `reference_syno_docker_orphaned_container`.
