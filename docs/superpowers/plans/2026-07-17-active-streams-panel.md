# Active Streams Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flickering per-provider socket-burst panel on the System Dashboard with a persistent "Active Streams" panel — one row per file being streamed, showing progress, buffer depth, and a cumulative per-provider byte breakdown.

**Architecture:** A heartbeat registry (`StreamSessionRegistry`) is upserted from the streaming hot path's existing `UpdateUsageContext` and reaped by TTL, so no fragile stream-dispose pairing is needed. A 1-second hosted broadcaster serializes fresh sessions to a new `str` websocket topic, enriching each with cumulative per-provider stats read from the in-memory `NzbProviderAffinityService`. The frontend renders these as persistent rows and drops the old `cxs` socket panel.

**Tech Stack:** C# .NET 10 (backend), xUnit (tests), React Router v7 + TypeScript + react-bootstrap (frontend), JSON over websocket.

## Global Constraints

- Registry write path (`Touch`) must stay O(1), allocation-light, and must never throw into the streaming loop — it runs inside `BufferedSegmentStream.UpdateUsageContext`. Protects the shipped OOM/perf work.
- No new locks or per-segment work on the fetch loop. `Touch` is a single `ConcurrentDictionary` upsert.
- Session freshness is TTL-based (15s). Correctness must not depend on catching every stream dispose path.
- Provider breakdown is **cumulative for the title** (affinity stats are persisted/reloaded per jobName), not per-session — label it that way in the UI ("total served"), never "this session".
- Broadcast only when `WebsocketManager.HasSubscribers` is true, mirroring existing producers.
- Version bump is PATCH. Update `VERSION`, `README.md` changelog, and the `backend/Program.cs` BUILD banner together (they must not disagree). Build `docker build -t local/nzbdav:3 .` when done; do not run/restart containers.
- Strip ANSI before grepping docker logs (not needed here; noted per repo convention).

---

## File Structure

**Backend (create):**
- `backend/Services/StreamSessionRegistry.cs` — heartbeat registry + session DTO + provider-breakdown enrichment + 1s broadcaster (hosted service).
- `backend.Tests/StreamSessionRegistryTests.cs` — unit tests for TTL, upsert, breakdown.

**Backend (modify):**
- `backend/Streams/BufferedSegmentStream.cs:562` — call `StreamSessionRegistry.Current?.Touch(...)` inside `UpdateUsageContext`.
- `backend/Websocket/WebsocketTopic.cs:6` — add `ActiveStreams` topic (`"str"`).
- `backend/Program.cs:248` — register `StreamSessionRegistry` as singleton + hosted service; eager-resolve it.
- `backend/Api/Controllers/Stats/StatsController.cs:65` — add `GET /api/stats/streams` SSR endpoint.
- `backend/Clients/Usenet/Connections/ConnectionPoolStats.cs:112` — stop the `cxs` UI push (dead consumer) while keeping counter updates.

**Frontend (create):**
- `frontend/app/types/streams.ts` — `StreamSession`, `StreamProviderTally` types.
- `frontend/app/routes/_index/components/dashboard/ActiveStreams.tsx` — the new panel.

**Frontend (modify):**
- `frontend/app/routes/_index/route.tsx:12` — loader fetches streams instead of connections.
- `frontend/app/clients/backend-client.server.ts` — add `getActiveStreams`.
- `frontend/app/routes/_index/components/dashboard/Dashboard.tsx` — subscribe to `str`, render `ActiveStreams`, remove `cxs`/`ActiveStreaming`.
- `frontend/app/routes/_index/components/dashboard/ActiveStreaming.tsx` — delete.

---

## Task 1: StreamSessionRegistry core (heartbeat + TTL)

**Files:**
- Create: `backend/Services/StreamSessionRegistry.cs`
- Test: `backend.Tests/StreamSessionRegistryTests.cs`

**Interfaces:**
- Produces:
  - `sealed class StreamSessionRegistry` with:
    - `void Touch(Guid davItemId, string fileName, string affinityKey, long currentBytePosition, long fileSize)`
    - `IReadOnlyList<ActiveStreamSnapshot> GetActiveSessions()`
    - `TimeSpan Ttl { get; init; }` (default 15s)
    - `static StreamSessionRegistry? Current { get; }` (set in constructor)
  - `sealed record ActiveStreamSnapshot(Guid DavItemId, string FileName, string AffinityKey, long CurrentBytePosition, long FileSize)`

- [ ] **Step 1: Write the failing test**

Create `backend.Tests/StreamSessionRegistryTests.cs`:

```csharp
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests;

public class StreamSessionRegistryTests
{
    private static StreamSessionRegistry NewRegistry(TimeSpan? ttl = null)
        => new StreamSessionRegistry { Ttl = ttl ?? TimeSpan.FromSeconds(15) };

    [Fact]
    public void Touch_AddsSession_AndGetActiveReturnsIt()
    {
        var reg = NewRegistry();
        var id = Guid.NewGuid();

        reg.Touch(id, "Movie.mkv", "movie", currentBytePosition: 100, fileSize: 1000);

        var sessions = reg.GetActiveSessions();
        Assert.Single(sessions);
        Assert.Equal(id, sessions[0].DavItemId);
        Assert.Equal("Movie.mkv", sessions[0].FileName);
        Assert.Equal(100, sessions[0].CurrentBytePosition);
        Assert.Equal(1000, sessions[0].FileSize);
    }

    [Fact]
    public void Touch_SameDavItem_CollapsesToOneSession_AndUpdatesPosition()
    {
        var reg = NewRegistry();
        var id = Guid.NewGuid();

        reg.Touch(id, "Movie.mkv", "movie", 100, 1000);
        reg.Touch(id, "Movie.mkv", "movie", 500, 1000);

        var sessions = reg.GetActiveSessions();
        Assert.Single(sessions);
        Assert.Equal(500, sessions[0].CurrentBytePosition);
    }

    [Fact]
    public void GetActiveSessions_ExcludesEntriesOlderThanTtl()
    {
        var reg = NewRegistry(TimeSpan.FromMilliseconds(1));
        reg.Touch(Guid.NewGuid(), "Old.mkv", "old", 1, 10);

        Thread.Sleep(20);

        Assert.Empty(reg.GetActiveSessions());
    }

    [Fact]
    public void Current_PointsAtMostRecentlyConstructedInstance()
    {
        var reg = NewRegistry();
        Assert.Same(reg, StreamSessionRegistry.Current);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter StreamSessionRegistryTests`
Expected: FAIL — `StreamSessionRegistry` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `backend/Services/StreamSessionRegistry.cs` (broadcaster added in Task 4 — core only here):

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;

namespace NzbWebDAV.Services;

/// <summary>
/// Heartbeat registry of in-progress stream sessions, keyed by DavItem so that seeks and
/// multipart parts of the same file collapse to a single session row. Entries are upserted from
/// the streaming path's existing UpdateUsageContext and expire by TTL, so correctness never
/// depends on catching a stream's dispose path.
/// </summary>
public sealed class StreamSessionRegistry
{
    private sealed class Entry
    {
        public string FileName = "";
        public string AffinityKey = "";
        public long CurrentBytePosition;
        public long FileSize;
        public long LastTouchedTimestamp;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Most recently constructed instance, reached from the manually-constructed
    /// BufferedSegmentStream which is not DI-managed. Mirrors BufferedSegmentStream's static config.</summary>
    public static StreamSessionRegistry? Current { get; private set; }

    public StreamSessionRegistry()
    {
        Current = this;
    }

    public void Touch(Guid davItemId, string fileName, string affinityKey, long currentBytePosition, long fileSize)
    {
        var entry = _sessions.GetOrAdd(davItemId, _ => new Entry());
        entry.FileName = fileName;
        entry.AffinityKey = affinityKey;
        entry.CurrentBytePosition = currentBytePosition;
        entry.FileSize = fileSize;
        entry.LastTouchedTimestamp = Stopwatch.GetTimestamp();
    }

    public IReadOnlyList<ActiveStreamSnapshot> GetActiveSessions()
    {
        var result = new List<ActiveStreamSnapshot>();
        foreach (var (davItemId, entry) in _sessions)
        {
            if (Stopwatch.GetElapsedTime(entry.LastTouchedTimestamp) > Ttl)
            {
                _sessions.TryRemove(davItemId, out _); // opportunistic sweep bounds the dict
                continue;
            }
            result.Add(new ActiveStreamSnapshot(
                davItemId, entry.FileName, entry.AffinityKey, entry.CurrentBytePosition, entry.FileSize));
        }
        return result;
    }
}

public sealed record ActiveStreamSnapshot(
    Guid DavItemId,
    string FileName,
    string AffinityKey,
    long CurrentBytePosition,
    long FileSize);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter StreamSessionRegistryTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add backend/Services/StreamSessionRegistry.cs backend.Tests/StreamSessionRegistryTests.cs
git commit -m "feat(streams): add heartbeat StreamSessionRegistry with TTL"
```

---

## Task 2: Wire Touch into the streaming path

**Files:**
- Modify: `backend/Streams/BufferedSegmentStream.cs:562` (inside `UpdateUsageContext`)

**Interfaces:**
- Consumes: `StreamSessionRegistry.Current`, `Touch(...)` from Task 1.

- [ ] **Step 1: Add the touch call**

In `backend/Streams/BufferedSegmentStream.cs`, inside `UpdateUsageContext()`, immediately after `details.CurrentBytePosition = (details.BaseByteOffset ?? 0) + _position;` (currently line ~571) and before the `var multiClient = GetMultiProviderClient(_client);` line, insert:

```csharp
        // Heartbeat the live-streams registry that powers the dashboard's Active Streams panel.
        // Keyed by DavItem so seeks/multipart parts of one file collapse to a single session.
        // Off the fetch loop (this method is called on progress, not per segment) and null-safe
        // so streaming tools/tests without a registry are unaffected.
        if (details.DavItemId is { } davItemId && details.FileSize is { } fileSize)
        {
            NzbWebDAV.Services.StreamSessionRegistry.Current?.Touch(
                davItemId,
                details.Text,
                _usageContext.Value.AffinityKey ?? details.Text,
                details.CurrentBytePosition ?? 0,
                fileSize);
        }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `cd backend && dotnet build`
Expected: Build succeeded (0 errors).

- [ ] **Step 3: Run the existing stream tests to verify no regression**

Run: `dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter BufferedSegmentStream`
Expected: PASS (existing dispose + prefetch-window tests still green).

- [ ] **Step 4: Commit**

```bash
git add backend/Streams/BufferedSegmentStream.cs
git commit -m "feat(streams): heartbeat StreamSessionRegistry from UpdateUsageContext"
```

---

## Task 3: Provider-breakdown enrichment DTO

**Files:**
- Modify: `backend/Services/StreamSessionRegistry.cs`
- Test: `backend.Tests/StreamSessionRegistryTests.cs`

**Interfaces:**
- Consumes: `NzbProviderAffinityService.GetJobStats(string) : Dictionary<int, NzbProviderStats>` (existing); `UsenetProviderConfig.Providers` for index→host mapping.
- Produces:
  - `sealed record StreamProviderTally(int ProviderIndex, string Host, long TotalBytes)`
  - `sealed record ActiveStreamDto(Guid DavItemId, string FileName, long CurrentBytePosition, long FileSize, int ProgressPercent, IReadOnlyList<StreamProviderTally> Providers)`
  - `static IReadOnlyList<ActiveStreamDto> BuildDtos(IReadOnlyList<ActiveStreamSnapshot> sessions, Func<string, Dictionary<int, NzbProviderStats>> jobStatsLookup, IReadOnlyList<string> providerHosts)`

- [ ] **Step 1: Write the failing test**

Append to `backend.Tests/StreamSessionRegistryTests.cs`:

```csharp
    [Fact]
    public void BuildDtos_MapsProviderIndexToHost_AndComputesProgress()
    {
        var id = Guid.NewGuid();
        var sessions = new[]
        {
            new ActiveStreamSnapshot(id, "Movie.mkv", "movie", CurrentBytePosition: 500, FileSize: 1000)
        };

        Dictionary<int, NzbWebDAV.Database.Models.NzbProviderStats> Lookup(string key) => new()
        {
            [0] = new() { JobName = key, ProviderIndex = 0, TotalBytes = 1_200_000_000 },
            [1] = new() { JobName = key, ProviderIndex = 1, TotalBytes = 380_000_000 },
        };
        var hosts = new[] { "news.frugalusenet.com", "news.newshosting.com" };

        var dtos = StreamSessionRegistry.BuildDtos(sessions, Lookup, hosts);

        Assert.Single(dtos);
        Assert.Equal(50, dtos[0].ProgressPercent);
        Assert.Equal(2, dtos[0].Providers.Count);
        // Sorted by bytes desc: frugalusenet first.
        Assert.Equal("news.frugalusenet.com", dtos[0].Providers[0].Host);
        Assert.Equal(1_200_000_000, dtos[0].Providers[0].TotalBytes);
    }

    [Fact]
    public void BuildDtos_DropsProviderIndicesOutsideCurrentConfig()
    {
        var id = Guid.NewGuid();
        var sessions = new[] { new ActiveStreamSnapshot(id, "Movie.mkv", "movie", 0, 1000) };

        // Index 9 is stale (config only has 2 providers) and must be dropped.
        Dictionary<int, NzbWebDAV.Database.Models.NzbProviderStats> Lookup(string key) => new()
        {
            [0] = new() { JobName = key, ProviderIndex = 0, TotalBytes = 10 },
            [9] = new() { JobName = key, ProviderIndex = 9, TotalBytes = 999 },
        };
        var hosts = new[] { "a", "b" };

        var dtos = StreamSessionRegistry.BuildDtos(sessions, Lookup, hosts);

        Assert.Single(dtos[0].Providers);
        Assert.Equal(0, dtos[0].Providers[0].ProviderIndex);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter StreamSessionRegistryTests`
Expected: FAIL — `BuildDtos`, `StreamProviderTally`, `ActiveStreamDto` do not exist.

- [ ] **Step 3: Write minimal implementation**

Append to `backend/Services/StreamSessionRegistry.cs` (add `using NzbWebDAV.Database.Models;` at top):

```csharp
public sealed record StreamProviderTally(int ProviderIndex, string Host, long TotalBytes);

public sealed record ActiveStreamDto(
    Guid DavItemId,
    string FileName,
    long CurrentBytePosition,
    long FileSize,
    int ProgressPercent,
    IReadOnlyList<StreamProviderTally> Providers);
```

And add this static method inside the `StreamSessionRegistry` class:

```csharp
    /// <summary>
    /// Enriches raw sessions with cumulative per-provider byte tallies (from affinity stats,
    /// which are per-title and persisted — NOT per-session) and maps provider indices to the
    /// current config's hosts, dropping stale indices from prior configs.
    /// </summary>
    public static IReadOnlyList<ActiveStreamDto> BuildDtos(
        IReadOnlyList<ActiveStreamSnapshot> sessions,
        Func<string, Dictionary<int, NzbProviderStats>> jobStatsLookup,
        IReadOnlyList<string> providerHosts)
    {
        var dtos = new List<ActiveStreamDto>(sessions.Count);
        foreach (var s in sessions)
        {
            var jobStats = jobStatsLookup(s.AffinityKey);
            var tallies = jobStats
                .Where(kv => kv.Key >= 0 && kv.Key < providerHosts.Count)
                .Where(kv => kv.Value.TotalBytes > 0)
                .Select(kv => new StreamProviderTally(kv.Key, providerHosts[kv.Key], kv.Value.TotalBytes))
                .OrderByDescending(t => t.TotalBytes)
                .ToList();

            var progress = s.FileSize > 0
                ? (int)Math.Clamp(s.CurrentBytePosition * 100 / s.FileSize, 0, 100)
                : 0;

            dtos.Add(new ActiveStreamDto(
                s.DavItemId, s.FileName, s.CurrentBytePosition, s.FileSize, progress, tallies));
        }
        return dtos;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter StreamSessionRegistryTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add backend/Services/StreamSessionRegistry.cs backend.Tests/StreamSessionRegistryTests.cs
git commit -m "feat(streams): enrich sessions with cumulative per-provider byte tallies"
```

---

## Task 4: Broadcast hosted service + topic + DI

**Files:**
- Modify: `backend/Websocket/WebsocketTopic.cs:6`
- Modify: `backend/Services/StreamSessionRegistry.cs` (add `IHostedService` broadcaster)
- Modify: `backend/Program.cs:248` and `:287`

**Interfaces:**
- Consumes: `WebsocketManager.HasSubscribers`, `WebsocketManager.SendMessage(WebsocketTopic, string)`; `NzbProviderAffinityService.GetJobStats`; `ConfigManager.GetUsenetProviderConfig()`; `BuildDtos` from Task 3.
- Produces: `WebsocketTopic.ActiveStreams` (name `"str"`); a 1s broadcast pushing a JSON `ActiveStreamDto[]`.

- [ ] **Step 1: Add the websocket topic**

In `backend/Websocket/WebsocketTopic.cs`, after the `UsenetConnections` line (line 6), add:

```csharp
    public static readonly WebsocketTopic ActiveStreams = new("str", TopicType.State);
```

- [ ] **Step 2: Make StreamSessionRegistry a hosted broadcaster**

Edit `backend/Services/StreamSessionRegistry.cs`: add usings and change the class declaration + constructor to inject dependencies and run a 1s timer. Replace the existing constructor with:

```csharp
    private readonly WebsocketManager _websocketManager;
    private readonly NzbProviderAffinityService _affinityService;
    private readonly ConfigManager _configManager;
    private readonly Timer _broadcastTimer;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    public StreamSessionRegistry(
        WebsocketManager websocketManager,
        NzbProviderAffinityService affinityService,
        ConfigManager configManager)
    {
        _websocketManager = websocketManager;
        _affinityService = affinityService;
        _configManager = configManager;
        Current = this;
        _broadcastTimer = new Timer(Broadcast, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Builds the current enriched stream list using live config + affinity stats.</summary>
    public IReadOnlyList<ActiveStreamDto> GetActiveStreamDtos()
    {
        var hosts = _configManager.GetUsenetProviderConfig().Providers.Select(p => p.Host).ToList();
        return BuildDtos(GetActiveSessions(), _affinityService.GetJobStats, hosts);
    }

    private void Broadcast(object? state)
    {
        // Nothing painting the panel -> skip the serialize entirely (mirrors other producers).
        if (!_websocketManager.HasSubscribers) return;
        var dtos = GetActiveStreamDtos();
        var json = System.Text.Json.JsonSerializer.Serialize(dtos, JsonOptions);
        _websocketManager.SendMessage(WebsocketTopic.ActiveStreams, json);
    }
```

Add these usings at the top of the file:

```csharp
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;
```

Note: the parameterless-constructor tests from Task 1/3 still compile because `Ttl` is an `init` property and `BuildDtos`/`Touch`/`GetActiveSessions` do not touch the injected fields. Keep a second parameterless constructor for tests:

```csharp
    // Test-only: builds a registry with no broadcaster wired up.
    public StreamSessionRegistry()
    {
        _websocketManager = null!;
        _affinityService = null!;
        _configManager = null!;
        _broadcastTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        Current = this;
    }
```

- [ ] **Step 3: Register in DI**

In `backend/Program.cs`, in the service registration chain (after `.AddSingleton<NzbProviderAffinityService>()`, line ~249), add:

```csharp
            .AddSingleton<StreamSessionRegistry>()
```

Then, alongside the eager `app.Services.GetRequiredService<BandwidthService>();` (line ~287), add:

```csharp
        app.Services.GetRequiredService<StreamSessionRegistry>();
```

Add `using NzbWebDAV.Services;` to `Program.cs` if not already present (grep first: `grep -n "using NzbWebDAV.Services" backend/Program.cs`).

- [ ] **Step 4: Build + run all backend tests**

Run: `cd backend && dotnet build && cd .. && dotnet test backend.Tests/NzbWebDAV.Tests.csproj`
Expected: Build succeeded; all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/Websocket/WebsocketTopic.cs backend/Services/StreamSessionRegistry.cs backend/Program.cs
git commit -m "feat(streams): broadcast active streams on the str websocket topic"
```

---

## Task 5: SSR endpoint for initial load

**Files:**
- Modify: `backend/Api/Controllers/Stats/StatsController.cs:65`

**Interfaces:**
- Consumes: `StreamSessionRegistry.GetActiveStreamDtos()` from Task 4 (inject `StreamSessionRegistry` into the controller).
- Produces: `GET /api/stats/streams` returning `ActiveStreamDto[]` as camelCase JSON.

- [ ] **Step 1: Inject the registry into the controller**

Confirm how `StatsController` receives services: `grep -n "public StatsController\|streamingClient\|configManager\|dbContext" backend/Api/Controllers/Stats/StatsController.cs | head`. Add a `StreamSessionRegistry streamSessionRegistry` parameter to its constructor / primary-constructor parameter list, matching the existing injection style used for `streamingClient`.

- [ ] **Step 2: Add the endpoint**

After the `GetActiveConnections` method (ends line ~183), add:

```csharp
    [HttpGet("streams")]
    public Task<IActionResult> GetActiveStreams()
    {
        return ExecuteSafely(() =>
        {
            var dtos = streamSessionRegistry.GetActiveStreamDtos();
            return Task.FromResult<IActionResult>(Ok(dtos));
        });
    }
```

(Replace `streamSessionRegistry` with whatever the injected field/parameter is named in Step 1.)

- [ ] **Step 3: Build to verify it compiles**

Run: `cd backend && dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add backend/Api/Controllers/Stats/StatsController.cs
git commit -m "feat(streams): add GET /api/stats/streams SSR endpoint"
```

---

## Task 6: Frontend types + ActiveStreams component

**Files:**
- Create: `frontend/app/types/streams.ts`
- Create: `frontend/app/routes/_index/components/dashboard/ActiveStreams.tsx`

**Interfaces:**
- Produces: `StreamSession`, `StreamProviderTally` types; `<ActiveStreams streams={StreamSession[]} />`.
- Consumes: JSON shape from Task 4/5 (camelCase `ActiveStreamDto`).

- [ ] **Step 1: Create the types**

Create `frontend/app/types/streams.ts`:

```ts
export type StreamProviderTally = {
    providerIndex: number;
    host: string;
    totalBytes: number;
};

export type StreamSession = {
    davItemId: string;
    fileName: string;
    currentBytePosition: number;
    fileSize: number;
    progressPercent: number;
    providers: StreamProviderTally[];
};
```

- [ ] **Step 2: Create the component**

Create `frontend/app/routes/_index/components/dashboard/ActiveStreams.tsx`:

```tsx
import { Card, ProgressBar } from 'react-bootstrap';
import type { StreamSession, StreamProviderTally } from '~/types/streams';

type Props = { streams: StreamSession[] };

export function ActiveStreams({ streams }: Props) {
    if (streams.length === 0) {
        return (
            <Card bg="dark" text="white" className="border-secondary mb-4">
                <Card.Body className="text-center text-muted py-4">
                    No active streams
                </Card.Body>
            </Card>
        );
    }

    return (
        <Card bg="dark" text="white" className="border-secondary mb-4">
            <Card.Body>
                <h6 className="text-muted mb-3">Active Streaming</h6>
                <div className="d-flex flex-column gap-3">
                    {streams.map(stream => (
                        <StreamRow key={stream.davItemId} stream={stream} />
                    ))}
                </div>
            </Card.Body>
        </Card>
    );
}

function StreamRow({ stream }: { stream: StreamSession }) {
    const name = stream.fileName.split('/').pop() || stream.fileName;
    return (
        <div className="bg-black bg-opacity-25 rounded p-3">
            <div className="d-flex justify-content-between align-items-center mb-1">
                <span className="fw-bold text-truncate" style={{ maxWidth: '70%' }} title={stream.fileName}>
                    {name}
                </span>
                <span className="text-muted small">{stream.progressPercent}%</span>
            </div>
            <ProgressBar now={stream.progressPercent} style={{ height: '4px' }} className="mb-2" />
            <div className="d-flex flex-wrap gap-2">
                {stream.providers.map(p => (
                    <ProviderBadge key={p.providerIndex} tally={p} />
                ))}
            </div>
            <div className="text-muted mt-1" style={{ fontSize: '0.7rem' }}>
                provider totals (cumulative for this title)
            </div>
        </div>
    );
}

function ProviderBadge({ tally }: { tally: StreamProviderTally }) {
    const host = tally.host.replace(/^news\.|^bonus\./, '');
    return (
        <span className="badge bg-secondary fw-normal">
            {host} · {formatBytes(tally.totalBytes)}
        </span>
    );
}

function formatBytes(bytes: number): string {
    if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`;
    if (bytes >= 1e6) return `${(bytes / 1e6).toFixed(0)} MB`;
    if (bytes >= 1e3) return `${(bytes / 1e3).toFixed(0)} KB`;
    return `${bytes} B`;
}
```

- [ ] **Step 3: Typecheck the new files**

Run: `cd frontend && npm run typecheck 2>&1 | grep -E "streams.ts|ActiveStreams.tsx" || echo "NO ERRORS in new files"`
Expected: `NO ERRORS in new files`.

- [ ] **Step 4: Commit**

```bash
git add frontend/app/types/streams.ts frontend/app/routes/_index/components/dashboard/ActiveStreams.tsx
git commit -m "feat(streams): add ActiveStreams dashboard panel"
```

---

## Task 7: Wire the panel in, drop the socket panel

**Files:**
- Modify: `frontend/app/clients/backend-client.server.ts`
- Modify: `frontend/app/routes/_index/route.tsx:12`
- Modify: `frontend/app/routes/_index/components/dashboard/Dashboard.tsx`
- Delete: `frontend/app/routes/_index/components/dashboard/ActiveStreaming.tsx`

**Interfaces:**
- Consumes: `getActiveStreams` (new client fn); `StreamSession` type; `str` websocket topic.

- [ ] **Step 1: Add the client fetch**

Inspect the existing connections fetch: `grep -n "connections\|getActiveConnections\|stats/connections\|export" frontend/app/clients/backend-client.server.ts | head`. Add a sibling function mirroring it, targeting `/api/stats/streams` and returning `StreamSession[]` (default `[]` on error). Name it `getActiveStreams`.

- [ ] **Step 2: Update the route loader**

In `frontend/app/routes/_index/route.tsx`, replace the connections fetch with the streams fetch:

```tsx
export async function loader({ request }: Route.LoaderArgs) {
    const [dashboardData, streams] = await Promise.all([
        getDashboardData(request),      // keep the existing dashboard-data call as-is
        getActiveStreams(request),
    ]);
    return { dashboardData, streams };
}
```

Update the import to pull `getActiveStreams` (and drop the old connections import if now unused), and update the component:

```tsx
export default function Index({ loaderData }: Route.ComponentProps) {
    const { dashboardData, streams } = loaderData;
    return (
        <Dashboard initialData={dashboardData} initialStreams={streams} />
    );
}
```

(Match the real name of the existing dashboard-data fetch found via `grep -n "loader" -A6 frontend/app/routes/_index/route.tsx`.)

- [ ] **Step 3: Rewire Dashboard.tsx**

In `frontend/app/routes/_index/components/dashboard/Dashboard.tsx`:

1. Replace imports:
```tsx
import type { StreamSession } from '~/types/streams';
import { ActiveStreams } from './ActiveStreams';
```
Remove `import { ActiveStreaming } from './ActiveStreaming';` and the `ConnectionUsageContext` import.

2. Change props + state:
```tsx
type Props = {
    initialData: DashboardData;
    initialStreams: StreamSession[];
};

export function Dashboard({ initialData, initialStreams }: Props) {
    const [data, setData] = useState(initialData);
    const [streams, setStreams] = useState(initialStreams);
```
Delete the `providerNames` reduce block (no longer used by this panel).

3. Replace the websocket `onmessage` body to consume `str` instead of `cxs`:
```tsx
            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== 'str') return;
                try {
                    setStreams(JSON.parse(message) as StreamSession[]);
                } catch (e) {
                    console.error('Failed to parse active-streams message', e);
                }
            });
```

4. Replace the panel render:
```tsx
            {/* Active Streaming - Full Width */}
            <ActiveStreams streams={streams} />
```

- [ ] **Step 4: Delete the old panel**

```bash
git rm frontend/app/routes/_index/components/dashboard/ActiveStreaming.tsx
```

- [ ] **Step 5: Typecheck (changed files clean)**

Run: `cd frontend && npm run typecheck 2>&1 | grep -E "Dashboard.tsx|route.tsx|backend-client" | grep -v "stats/route.tsx" || echo "NO NEW ERRORS"`
Expected: `NO NEW ERRORS` (the pre-existing `stats/route.tsx` / `ProviderStatus.tsx` errors are unrelated and out of scope).

- [ ] **Step 6: Commit**

```bash
git add frontend/app
git commit -m "feat(streams): render ActiveStreams from str topic, remove socket panel"
```

---

## Task 8: Retire the dead cxs push, version + ship

**Files:**
- Modify: `backend/Clients/Usenet/Connections/ConnectionPoolStats.cs:112`
- Modify: `VERSION`, `README.md`, `backend/Program.cs` (BUILD banner)

- [ ] **Step 1: Confirm no remaining cxs consumer**

Run: `grep -rn "'cxs'\|\"cxs\"\|UsenetConnections\|GetActiveConnectionsByProvider\|stats/connections" frontend/app backend --include=*.tsx --include=*.ts --include=*.cs | grep -v build | grep -v test`
Expected: no frontend consumer of `cxs` remains (backend push sites + the now-unused `/connections` endpoint may remain; that is fine). If any frontend `cxs` consumer remains, STOP and reconcile before disabling the push.

- [ ] **Step 2: Stop building the cxs UI payload**

In `backend/Clients/Usenet/Connections/ConnectionPoolStats.cs`, in `GetOnConnectionPoolChanged`'s `OnEvent`, the counter block (updating `_live`/`_idle`/`_totalLive`) MUST stay. Replace the trailing UI-push section (currently the `if (!_websocketManager.HasSubscribers) return; SchedulePush(providerIndex, args);` at ~line 112-114) with an early return so the ~60 KB/push serialization never runs:

```csharp
            // The Active Streams panel replaced the per-provider socket panel, so nothing consumes
            // the cxs topic anymore. Skip the ~60 KB/push serialization entirely; the cheap counter
            // updates above still run for any other reader.
            return;
```

Leave `SchedulePush`, `FlushAfterDelayAsync`, and `Push` in place (dead but harmless; removing them is out of scope and risks the coalescing logic).

- [ ] **Step 3: Build + full test run**

Run: `cd backend && dotnet build && cd .. && dotnet test backend.Tests/NzbWebDAV.Tests.csproj`
Expected: Build succeeded; all tests PASS (including `ConnectionPoolStatsAllocationTests`).

- [ ] **Step 4: Bump version + changelog + banner**

Edit `VERSION` to `0.11.8` (assuming 0.11.7 shipped; if `cat VERSION` shows otherwise, use next PATCH).

In `backend/Program.cs`, update the BUILD banner line to:
```csharp
        Log.Warning("  NzbDav Backend Starting - BUILD v2026-07-17-ACTIVE-STREAMS-PANEL");
```

In `README.md`, insert below `## Changelog`:
```markdown
## v0.11.8 (2026-07-17)
Replaces the flickering per-provider socket panel on the dashboard with a persistent Active Streams panel.

### UI
*   **Feature (persistent Active Streams panel)**: the old panel measured instantaneous borrowed sockets, which for a well-buffered stream sit idle between prefetch bursts — so it read "No active streams" during active playback. A new heartbeat registry tracks stream sessions keyed by file and survives the fetch/coast sawtooth, so the panel shows one stable row per streaming file with progress and a cumulative per-provider byte breakdown (from affinity stats). The strobing socket-burst panel is removed.
*   **Performance (dropped the dead cxs push)**: with the socket panel gone, the per-connection-borrow websocket serialization (~60 KB/push, historically the largest streaming-path allocator) no longer runs.
```

- [ ] **Step 5: Build the Docker image**

Run: `docker build -t local/nzbdav:3 .`
Expected: image built. Do NOT run/restart the container — inform the user it is ready to test.

- [ ] **Step 6: Commit**

```bash
git add VERSION README.md backend/Program.cs backend/Clients/Usenet/Connections/ConnectionPoolStats.cs
git commit -m "perf(streams): retire dead cxs push; ship v0.11.8 active streams panel"
```

---

## Self-Review Notes

- **Spec coverage:** session panel (Tasks 1–2, 6), cumulative provider breakdown folded into rows (Tasks 3, 6), socket panel dropped (Tasks 7–8), cheap affinity source confirmed (Task 3 uses `GetJobStats`). All covered.
- **Type consistency:** `ActiveStreamDto` (camelCased over the wire) ↔ `StreamSession`; `StreamProviderTally` fields (`providerIndex`, `host`, `totalBytes`) match both sides. `Touch` signature identical in Tasks 1 and 2. `GetActiveStreamDtos` defined in Task 4, consumed in Tasks 4 (broadcast) and 5 (SSR).
- **Risk controls:** heartbeat + TTL (no dispose-path dependency); `Touch` off the fetch loop and null-safe; broadcast gated on `HasSubscribers`; cxs push disabled to preserve the prior allocation win; provider breakdown explicitly labeled cumulative.
- **Open confirmations deferred to execution (grep-first steps):** exact `StatsController` injection style (Task 5 Step 1), existing dashboard-data loader fn name (Task 7 Step 2), `backend-client.server.ts` fetch pattern (Task 7 Step 1), `Program.cs` usings (Task 4 Step 3).
