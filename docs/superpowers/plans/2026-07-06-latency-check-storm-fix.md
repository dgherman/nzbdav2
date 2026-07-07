# Latency-check storm fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the unthrottled latency-check "storm" that wedges the nzbdav2 backend on reboot cold-start, by adding a fixed-cadence + single-flight gate to the per-provider latency check.

**Architecture:** Extract the fire decision into a tiny standalone `LatencyCheckGate` (throttle + single-flight), unit-test it in isolation, then have `MultiConnectionNntpClient.CheckLatency` delegate to it. No changes to the connection pool or custom semaphores.

**Tech Stack:** C# / .NET 10, xUnit 2.9. Local dotnet: `/opt/homebrew/opt/dotnet/bin/dotnet` (Homebrew .NET 10 on this Mac).

## Global Constraints

- Target framework: `net10.0` (backend + tests).
- Local build/test binary: `/opt/homebrew/opt/dotnet/bin/dotnet` (NOT bare `dotnet`).
- Test project `backend.Tests/NzbWebDAV.Tests.csproj` has **no `InternalsVisibleTo`** → any type it tests must be `public`.
- Do NOT build or push Docker images locally — CI builds images on push. Local `dotnet build`/`dotnet test` only.
- Scope guard: do NOT modify `PrioritizedSemaphore`, `ExtendedSemaphoreSlim`, `CombinedSemaphoreSlim`, or `GlobalOperationLimiter`. This change is confined to the latency-check path.
- Throttle interval: **45 seconds** (matches current healthy cadence).
- Fork release ritual (ARR.md): every code change bumps `VERSION`, adds a `README.md` changelog entry, and updates the `BUILD v...` string in `backend/Program.cs`.

---

### Task 1: `LatencyCheckGate` (throttle + single-flight) — TDD

**Files:**
- Create: `backend/Clients/Usenet/Connections/LatencyCheckGate.cs`
- Test: `backend.Tests/LatencyCheckGateTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class LatencyCheckGate` in namespace `NzbWebDAV.Clients.Usenet.Connections`
  - `public LatencyCheckGate(TimeSpan minInterval)`
  - `public bool TryBegin(DateTimeOffset now)` — returns `true` only if `(now - lastAttempt) > minInterval` AND no check in flight; on `true` it records `now` as the attempt time and sets in-flight.
  - `public void End()` — clears the in-flight flag.

- [ ] **Step 1: Write the failing tests**

Create `backend.Tests/LatencyCheckGateTests.cs`:

```csharp
using System;
using NzbWebDAV.Clients.Usenet.Connections;
using Xunit;

namespace NzbWebDAV.Tests;

public class LatencyCheckGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    private static DateTimeOffset T(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Fact]
    public void FirstCall_Begins()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
    }

    [Fact]
    public void SecondCall_WhileInFlight_IsBlocked()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        Assert.False(gate.TryBegin(T(0))); // single-flight: previous not ended
    }

    [Fact]
    public void AfterEnd_WithinInterval_IsThrottled()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End();
        Assert.False(gate.TryBegin(T(20))); // 20s <= 45s
    }

    [Fact]
    public void AfterEnd_PastInterval_Begins()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End();
        Assert.True(gate.TryBegin(T(46))); // 46s > 45s
    }

    [Fact]
    public void End_ReleasesFlag_EvenAfterSimulatedFailure()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End(); // ping finished/threw -> finally released the flag
        Assert.True(gate.TryBegin(T(50))); // released AND past interval
    }

    [Fact]
    public void ExactInterval_IsStillThrottled()
    {
        var gate = new LatencyCheckGate(Interval);
        Assert.True(gate.TryBegin(T(0)));
        gate.End();
        Assert.False(gate.TryBegin(T(45))); // boundary: <= is throttled
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter "FullyQualifiedName~LatencyCheckGate"`
Expected: FAIL — compile error, `LatencyCheckGate` type does not exist.

- [ ] **Step 3: Implement `LatencyCheckGate`**

Create `backend/Clients/Usenet/Connections/LatencyCheckGate.cs`:

```csharp
using System;
using System.Threading;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Throttle + single-flight gate for the per-provider latency check.
/// Guarantees at most one latency check runs at a time and that attempts
/// (whether they succeed OR fail) occur no more often than <c>minInterval</c>.
/// Without this, a provider that is unreachable never advances the
/// success-based throttle, so the 10s timer fires an unthrottled ping every
/// tick — the "latency-check storm" that wedged the backend on reboot.
/// Designed for a single timer caller; not a general-purpose primitive.
/// </summary>
public sealed class LatencyCheckGate
{
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private int _inFlight; // 0 = idle, 1 = running

    public LatencyCheckGate(TimeSpan minInterval) => _minInterval = minInterval;

    /// <summary>
    /// Attempts to begin a latency check. On <c>true</c>, records <paramref name="now"/>
    /// as the attempt time and marks in-flight; the caller MUST call <see cref="End"/>
    /// when the check completes (in a finally). Returns <c>false</c> when throttled or
    /// when a check is already in flight.
    /// </summary>
    public bool TryBegin(DateTimeOffset now)
    {
        if (now - _lastAttempt <= _minInterval) return false;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return false;
        _lastAttempt = now;
        return true;
    }

    /// <summary>Releases the single-flight flag. Call once per successful <see cref="TryBegin"/>.</summary>
    public void End() => Interlocked.Exchange(ref _inFlight, 0);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter "FullyQualifiedName~LatencyCheckGate"`
Expected: PASS — 6 tests passed.

- [ ] **Step 5: Commit**

```bash
cd ~/Documents/projects/nzbdav2
git add backend/Clients/Usenet/Connections/LatencyCheckGate.cs backend.Tests/LatencyCheckGateTests.cs
git commit -m "feat(usenet): add LatencyCheckGate (throttle + single-flight)"
```

---

### Task 2: Wire `LatencyCheckGate` into `CheckLatency`

**Files:**
- Modify: `backend/Clients/Usenet/MultiConnectionNntpClient.cs` (field declarations near line 39–40; `CheckLatency` at lines 90–120)

**Interfaces:**
- Consumes: `LatencyCheckGate.TryBegin(DateTimeOffset)`, `LatencyCheckGate.End()` (Task 1).

- [ ] **Step 1: Verify the old throttle field has no other reader**

Run: `grep -nE '_lastLatencyRecordTime' backend/Clients/Usenet/MultiConnectionNntpClient.cs`
Expected: the only READ is inside `CheckLatency` (the `<= 45s` guard); the remaining hits are the declaration and the write(s) on successful ops. Confirm no other code branches on it. (If an unexpected reader exists, stop and reconcile before editing.)

- [ ] **Step 2: Add the gate field**

In `backend/Clients/Usenet/MultiConnectionNntpClient.cs`, next to the existing latency fields (`_lastLatencyRecordTime`, `_latencyMonitorTimer`, ~lines 39–40), add:

```csharp
    private readonly NzbWebDAV.Clients.Usenet.Connections.LatencyCheckGate _latencyGate = new(TimeSpan.FromSeconds(45));
```

(If a `using NzbWebDAV.Clients.Usenet.Connections;` is already present at the top of the file, use the short name `new LatencyCheckGate(TimeSpan.FromSeconds(45))` instead.)

- [ ] **Step 3: Replace `CheckLatency` with the gated version**

Replace the entire existing `CheckLatency` method (currently lines ~90–120) with:

```csharp
    private void CheckLatency(object? state)
    {
        // Fixed-cadence throttle + single-flight. Prevents the latency-check storm:
        // a failing provider previously fired an unthrottled ping every 10s because
        // the old throttle only advanced on success.
        if (!_latencyGate.TryBegin(DateTimeOffset.UtcNow)) return;

        Task.Run(async () =>
        {
            try
            {
                // Short timeout for the ping.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                // Label the op as an Analysis provider health-check/ping.
                using var _ = cts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Analysis, "Latency Check"));
                await DateAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Debug("Latency check (ping) failed for provider {Host}: {Error}", _host, ex.Message);
            }
            finally
            {
                _latencyGate.End(); // release single-flight even if the ping threw
            }
        });
    }
```

Notes:
- The old outer `try/catch` ("Error initiating latency check") is dropped — `TryBegin` is non-throwing and `Task.Run` does not throw synchronously here.
- `_lastLatencyRecordTime` and its write on successful ops are **left untouched** (they still timestamp the last successful operation); only its use as the throttle is removed. Do not delete it — that would touch the streaming hot path unnecessarily.

- [ ] **Step 4: Build + run the full test suite (no regressions)**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj`
Expected: Build succeeded, 0 errors.

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj`
Expected: PASS — all tests (including the 6 `LatencyCheckGate` tests) pass.

- [ ] **Step 5: Commit**

```bash
cd ~/Documents/projects/nzbdav2
git add backend/Clients/Usenet/MultiConnectionNntpClient.cs
git commit -m "fix(usenet): gate latency check with fixed cadence + single-flight

Failing providers previously fired an unthrottled 10s ping (throttle
only advanced on success) with no single-flight guard, storming the
connection/semaphore layer and wedging the backend on reboot cold-start.
CheckLatency now delegates to LatencyCheckGate (45s cadence, one in flight)."
```

---

### Task 3: Version, changelog, and BUILD-string bump

**Files:**
- Modify: `VERSION`
- Modify: `README.md` (changelog section, above `## v0.11.0 (2026-06-24)` at line ~119)
- Modify: `backend/Program.cs` (`BUILD v...` at line 62)

- [ ] **Step 1: Bump `VERSION`**

Set the contents of `VERSION` to exactly:

```
0.11.1
```

- [ ] **Step 2: Add changelog entry**

In `README.md`, immediately above the `## v0.11.0 (2026-06-24)` line, insert:

```markdown
## v0.11.1 (2026-07-06)
- Fix: latency check no longer storms unreachable providers. A failing provider previously fired an unthrottled ping every 10s (the throttle only advanced on a successful ping) with no single-flight guard, which could wedge the backend on reboot cold-start (unresponsive `/health`, restart loop). The per-provider latency check now runs at a fixed ~45s cadence with a single-flight guard (`LatencyCheckGate`).

```

- [ ] **Step 3: Update the BUILD string**

In `backend/Program.cs` line 62, replace:

```csharp
        Log.Warning("  NzbDav Backend Starting - BUILD v2026-06-23-PROVIDER-CIRCUIT-BREAKER");
```

with:

```csharp
        Log.Warning("  NzbDav Backend Starting - BUILD v2026-07-06-LATENCY-CHECK-SINGLEFLIGHT");
```

- [ ] **Step 4: Verify build still compiles**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
cd ~/Documents/projects/nzbdav2
git add VERSION README.md backend/Program.cs
git commit -m "chore(release): v0.11.1 — latency-check storm fix"
```

---

### Task 4: Deploy (push → CI → verify → merge → NAS)

Not code — executed interactively with the user (needs CI wait + NAS restart). Listed so nothing is missed.

- [ ] **Step 1: Push the branch to trigger a CI image build**

```bash
cd ~/Documents/projects/nzbdav2
git push -u origin fix/latency-check-storm
```

- [ ] **Step 2: Wait for CI** — watch the Actions run for `fix/latency-check-storm`; confirm the image builds and any CI tests pass.

- [ ] **Step 3: Verify on the test image** (if a test tag is produced) or after merge on the NAS: backend boots `healthy`, `/health` fast, and under a deliberately-unreachable provider the logs show latency-check attempts ~45s apart (not every 10s) with no permit-storm spam.

- [ ] **Step 4: Merge to master (only after user confirms the test build is good)** — per ARR.md upstream/deploy workflow, master push builds `:latest`.

- [ ] **Step 5: Pull + restart on the NAS**

```bash
ssh syno -o RequestTTY=no -o RemoteCommand=none \
  "cd /volume1/docker && sudo /usr/local/bin/docker compose pull nzbdav2 && sudo /usr/local/bin/docker compose up -d nzbdav2"
```

- [ ] **Step 6: Post-deploy watch** — confirm `Up … (healthy)`, `/health` 200 in ~ms, and no recurrence over ~10 min.

## Self-Review

- **Spec coverage:** throttle-on-attempt + single-flight → Tasks 1–2; keep `_lastLatencyRecordTime` writes / move only the gate → Task 2 Steps 1 & 3; non-goals (no semaphore edits) → Global Constraints; TDD cases 1–5 from spec → Task 1 tests (plus an exact-boundary case); deploy ritual → Tasks 3–4. All covered.
- **Placeholders:** none — all code and commands are literal.
- **Type consistency:** `LatencyCheckGate` / `TryBegin(DateTimeOffset)` / `End()` used identically in Task 1 (definition + tests) and Task 2 (call site). `45s` interval consistent across gate ctor, wiring, and changelog copy.
