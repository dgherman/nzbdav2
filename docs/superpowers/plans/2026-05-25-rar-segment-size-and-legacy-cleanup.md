# RAR/Multipart Segment-Size Fix + Legacy DavRarFile Migration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** (1) Migrate legacy `DavRarFile` rows forward to `DavMultipartFile` and remove the legacy serving code, leaving one streaming model; (2) fix RAR/multipart seeking by giving `NzbFileStream` exact decoded per-segment offsets, persisted on `DavMultipartFile.FilePart`, populated lazily + eagerly.

**Architecture:** RAR/multipart content is served by `DatabaseStoreMultipartFile` → `DavMultipartFileStream`, which currently passes **no** `segmentSizes` to `NzbFileStream` → `_segmentOffsets` is null → slow per-seek yEnc interpolation + uniform-approx byte accounting → Stremio tail-probe times out (>60 s) and the stream serves 0 bytes against a declared Content-Length (`Response Content-Length mismatch: too few bytes written (0 of …)`). A parallel legacy path (`DavRarFile`/`DatabaseStoreRarFile`, `ItemType.RarFile`) is no longer created (replaced by `DavMultipartFile` at commit `b13caec`) but still serves pre-existing DB rows and has the same seek bug with nowhere to store sizes. This plan converts those rows to `DavMultipartFile` (so they also get the fix), removes the legacy *serving* code, adds `long[]? SegmentSizes` to `DavMultipartFile.FilePart` (stored in the existing compressed-JSON column → **no EF migration**), and populates it. A size array that does not sum exactly to the part size is rejected, so wrong data can never be served.

**Staged deletion (data safety):** This release keeps the `DavRarFile` model + `RarFiles` DbSet + `ToDavMultipartFileMeta()` + table + the `ItemType.RarFile` enum value, used **only** by the one-time conversion task. A small follow-up release (out of scope here, see "Follow-up release" section) drops the table and deletes the model after rollout. This protects users who skip versions.

**Tech Stack:** C# / .NET 10, EF Core (SQLite, JSON value-converter columns), Serilog, xUnit (new test project), `dotnet` at `/opt/homebrew/opt/dotnet/bin/dotnet`.

---

## Verified call chain & facts (read before starting)

- Serve: `WebDav/DatabaseStoreMultipartFile.cs:56-63` → `DavMultipartFileStream(Metadata.FileParts)`. Per part `Streams/DavMultipartFileStream.cs:101-112` calls `usenet.GetFileStream(part.SegmentIds, part.SegmentIdByteRange.Count, …)` **without** `segmentSizes`, then `stream.Seek(part.FilePartByteRange.StartInclusive)`.
- Offset build/validate: `Streams/NzbFileStream.cs:68-85` (`_segmentOffsets` only if sizes sum to `_fileSize`). Seek `:243-274`; base offset `:325-328`.
- Persistence: `Database/Models/DavMultipartFile.cs` (`FilePart` = `SegmentIds`, `SegmentIdByteRange`, `FilePartByteRange`, `SegmentFallbacks`); stored compressed-JSON in `Database/DavDatabaseContext.cs:345-357`. Adding a nullable field = serialization-only, **no migration**; old rows → `null`.
- Decoded sizes: `Clients/Usenet/UsenetStreamingClient.cs:88-207` `AnalyzeNzbAsync` returns decoded `header.PartSize[]` (uniform fast path = 3 fetches; else N-fetch scan; DMCA fail-fast). Sizes overload: `GetFileStream(…, long[]? segmentSizes = null, Dictionary<int,string[]>? segmentFallbacks = null, …)` at `:291-297`.
- Legacy conversion already exists: `Database/Models/DavRarFile.cs` `ToDavMultipartFileMeta()` → `DavMultipartFile.Meta`.
- `ItemType` stored as `int` with explicit values (`NzbFile=3, RarFile=4, MultipartFile=6`, `DavItem.cs:59-66`) — removing branches is safe; the enum value stays this release.
- Legacy creators: NONE (`new DavRarFile` / `RarFiles.Add` / `type: ItemType.RarFile` absent). All current aggregators write `DavMultipartFile`.
- `MultipartFileStream` / `MultipartFile` (`Models/MultipartFile.cs`) are **live** (`SevenZipProcessor.cs:46` processing). Do NOT touch them.
- Startup: normal path does not call `MigrateAsync` (only the `--db-migration` entrypoint at `Program.cs:131-168` does); the conversion task runs in the normal path after the PRAGMA block (`Program.cs:178`).

---

## File Structure

- **Create** `backend.Tests/NzbWebDAV.Tests.csproj` + tests.
- **Create** `backend/Streams/SegmentOffsetTable.cs` — pure offset build/validate (extracted from `NzbFileStream`).
- **Create** `backend/WebDav/SegmentSizePopulation.cs` — pure decide/validate helpers for lazy population.
- **Create** `backend/Database/LegacyRarFileMigration.cs` — idempotent one-time RarFile→MultipartFile data task.
- **Modify** `Streams/NzbFileStream.cs`, `Database/Models/DavMultipartFile.cs`, `Streams/DavMultipartFileStream.cs`, `WebDav/DatabaseStoreMultipartFile.cs`, `Program.cs`.
- **Modify (remove legacy serving)** `WebDav/DatabaseStoreCollection.cs`, `WebDav/DatabaseStoreIdFile.cs`, `WebDav/DatabaseStoreSymlinkCollection.cs`, `Database/DavDatabaseClient.cs`, `Services/HealthCheckService.cs`, `Queue/PostProcessors/BlacklistedExtensionPostProcessor.cs`, `Api/Controllers/TestDownload/TestDownloadController.cs`, `Api/Controllers/GetFileDetails/GetFileDetailsController.cs`, `Api/Controllers/ProviderBenchmark/ProviderBenchmarkController.cs`, `Api/Controllers/DownloadNzb/DownloadNzbController.cs`, `Api/Controllers/Maintenance/RepairClassificationController.cs`.
- **Delete** `WebDav/DatabaseStoreRarFile.cs`.
- **Modify (Phase 3 eager)** `Queue/FileProcessors/RarProcessor.cs`, `Queue/FileAggregators/RarAggregator.cs`.
- **Modify (release)** `VERSION`, `README.md`, `backend/Program.cs` (`BUILD v…`).
- **Keep (legacy, for conversion task only)** `Database/Models/DavRarFile.cs`, `RarFiles` DbSet + JSON mapping in `DavDatabaseContext.cs`, `ItemType.RarFile` — add a `// LEGACY (remove in follow-up)` comment to each.

---

## PHASE 0 — Test harness

### Task 1: Test project scaffold

**Files:** Create `backend.Tests/NzbWebDAV.Tests.csproj`, `backend.Tests/SmokeTest.cs`

- [ ] **Step 1: Create the test project**

`backend.Tests/NzbWebDAV.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../backend/NzbWebDAV.csproj" />
  </ItemGroup>
</Project>
```
> If the `Microsoft.EntityFrameworkCore.Sqlite` version `10.0.0` does not resolve, match the version already used by `backend/NzbWebDAV.csproj` (grep it: `grep EntityFrameworkCore backend/NzbWebDAV.csproj`).

`backend.Tests/SmokeTest.cs`:
```csharp
namespace NzbWebDAV.Tests;

public class SmokeTest
{
    [Fact]
    public void ProjectBuildsAndTestsRun() => Assert.True(true);
}
```

- [ ] **Step 2: Run it**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj -v minimal`
Expected: PASS (1 test). If the backend fails to compile, fix that first.

- [ ] **Step 3: Commit**

```bash
git add backend.Tests/NzbWebDAV.Tests.csproj backend.Tests/SmokeTest.cs
git commit -m "test: add NzbWebDAV.Tests xUnit project"
```

---

## PHASE 1 — Legacy data migration (RarFile → MultipartFile)

### Task 2: Idempotent conversion task + wire into startup

**Files:**
- Create: `backend/Database/LegacyRarFileMigration.cs`
- Create: `backend.Tests/LegacyRarFileMigrationTests.cs`
- Modify: `backend/Program.cs` (after line 178)
- Modify (comment only): `backend/Database/Models/DavRarFile.cs`, `backend/Database/DavDatabaseContext.cs` (RarFiles mapping), `backend/Database/Models/DavItem.cs` (`RarFile = 4`)

- [ ] **Step 1: Write the failing conversion-mapping test (uses SQLite in-memory)**

`backend.Tests/LegacyRarFileMigrationTests.cs`:
```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests;

public class LegacyRarFileMigrationTests
{
    private static DavDatabaseContext NewInMemoryCtx(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(conn).Options;
        var ctx = new DavDatabaseContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task ConvertsRarFileRowToMultipartFile_AndFlipsType_AndIsIdempotent()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var itemId = Guid.NewGuid();
        using (var ctx = NewInMemoryCtx(conn))
        {
            var dir = DavItem.Root; // parent not important for this test
            var item = DavItem.New(itemId, DavItem.Root, "movie.mkv", 1000,
                DavItem.ItemType.RarFile, DateTimeOffset.UtcNow, null, null);
            ctx.Items.Add(item);
            ctx.RarFiles.Add(new DavRarFile
            {
                Id = itemId,
                RarParts = new[]
                {
                    new DavRarFile.RarPart
                    {
                        SegmentIds = new[] { "a@x", "b@x" },
                        PartSize = 1000, Offset = 0, ByteCount = 1000, ObfuscationKey = null
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        using (var ctx = NewInMemoryCtx(conn))
        {
            var converted = await LegacyRarFileMigration.RunAsync(ctx);
            Assert.Equal(1, converted);
        }

        using (var ctx = NewInMemoryCtx(conn))
        {
            Assert.False(await ctx.RarFiles.AnyAsync());
            var mp = await ctx.MultipartFiles.FirstOrDefaultAsync(x => x.Id == itemId);
            Assert.NotNull(mp);
            Assert.Equal(new[] { "a@x", "b@x" }, mp!.Metadata.FileParts[0].SegmentIds);
            var item = await ctx.Items.FirstAsync(x => x.Id == itemId);
            Assert.Equal(DavItem.ItemType.MultipartFile, item.Type);
        }

        // Idempotent: second run converts nothing.
        using (var ctx = NewInMemoryCtx(conn))
        {
            Assert.Equal(0, await LegacyRarFileMigration.RunAsync(ctx));
        }
    }
}
```
> Implementer note: this requires `DavDatabaseContext` to accept `DbContextOptions`. If it currently only has a parameterless constructor with hardcoded `OnConfiguring`, add a constructor `public DavDatabaseContext(DbContextOptions<DavDatabaseContext> options) : base(options) { }` and guard `OnConfiguring` with `if (!optionsBuilder.IsConfigured) { …existing… }`. Also confirm the exact `DavItem.New(...)` parameter list and adjust the call (search `public static DavItem New(`). If a parameter differs, fix the test call — do not change production signatures to suit the test beyond the options constructor.

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter LegacyRarFileMigrationTests -v minimal`
Expected: FAIL to compile — `LegacyRarFileMigration` does not exist.

- [ ] **Step 3: Implement the conversion task**

`backend/Database/LegacyRarFileMigration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// LEGACY (remove in follow-up release): one-time, idempotent conversion of pre-v0.8.0
/// DavRarFile rows into DavMultipartFile rows. RarFile items are no longer created; this
/// migrates existing user data forward so it uses the single multipart streaming model and
/// benefits from per-segment seek offsets. Safe to run on every startup — converts only rows
/// that still exist and returns the count converted.
/// </summary>
public static class LegacyRarFileMigration
{
    public static async Task<int> RunAsync(DavDatabaseContext ctx, CancellationToken ct = default)
    {
        var rarFiles = await ctx.RarFiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (rarFiles.Count == 0) return 0;

        Log.Warning("[LegacyRarFileMigration] Converting {Count} legacy DavRarFile rows to DavMultipartFile...", rarFiles.Count);

        var converted = 0;
        foreach (var rarFile in rarFiles)
        {
            var item = await ctx.Items.FirstOrDefaultAsync(x => x.Id == rarFile.Id, ct).ConfigureAwait(false);
            if (item == null)
            {
                // Orphan rar row with no DavItem — drop it.
                var orphan = await ctx.RarFiles.FirstAsync(x => x.Id == rarFile.Id, ct).ConfigureAwait(false);
                ctx.RarFiles.Remove(orphan);
                continue;
            }

            // Skip if a MultipartFile row already exists for this id (partial prior run).
            if (!await ctx.MultipartFiles.AnyAsync(x => x.Id == rarFile.Id, ct).ConfigureAwait(false))
            {
                ctx.MultipartFiles.Add(new DavMultipartFile
                {
                    Id = rarFile.Id,
                    Metadata = rarFile.ToDavMultipartFileMeta(),
                });
            }

            item.Type = DavItem.ItemType.MultipartFile;

            var tracked = await ctx.RarFiles.FirstAsync(x => x.Id == rarFile.Id, ct).ConfigureAwait(false);
            ctx.RarFiles.Remove(tracked);
            converted++;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Warning("[LegacyRarFileMigration] Converted {Count} DavRarFile rows.", converted);
        return converted;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter LegacyRarFileMigrationTests -v minimal`
Expected: PASS (1 test). If `DavItem.New` signature mismatched, fix the test call and re-run.

- [ ] **Step 5: Wire into normal startup**

In `backend/Program.cs`, immediately after line 178 (`PRAGMA busy_timeout = 5000;`), add:
```csharp
        // One-time legacy data migration: convert any pre-v0.8.0 DavRarFile rows to DavMultipartFile.
        // Idempotent; no-op once converted. Runs before the WebDAV server starts serving.
        await NzbWebDAV.Database.LegacyRarFileMigration.RunAsync(databaseContext).ConfigureAwait(false);
```

- [ ] **Step 6: Add LEGACY markers (comment-only)**

- In `backend/Database/Models/DavRarFile.cs`, above `public class DavRarFile`, add:
```csharp
// LEGACY (remove in follow-up release after rollout): no longer created. Retained only so
// LegacyRarFileMigration can convert pre-v0.8.0 rows to DavMultipartFile. Do not add new usages.
```
- In `backend/Database/DavDatabaseContext.cs`, above the `// DavRarFile` block (`:307`), add:
```csharp
        // LEGACY (remove in follow-up release): RarFiles table kept only for LegacyRarFileMigration.
```
- In `backend/Database/Models/DavItem.cs`, change `RarFile = 4,` to:
```csharp
        RarFile = 4, // LEGACY: migrated to MultipartFile (v0.8.0). Enum value kept for int stability.
```

- [ ] **Step 7: Build + commit**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj -v minimal`
Expected: Build succeeded.
```bash
git add backend/Database/LegacyRarFileMigration.cs backend.Tests/LegacyRarFileMigrationTests.cs backend/Program.cs backend/Database/Models/DavRarFile.cs backend/Database/DavDatabaseContext.cs backend/Database/Models/DavItem.cs
git commit -m "feat: idempotent startup migration of legacy DavRarFile rows to DavMultipartFile"
```

---

## PHASE 2 — Remove legacy serving code

After Phase 1, no `RarFile` items exist at serve time. Remove the serving/branching code and the `DatabaseStoreRarFile` store. The `DavRarFile` model/DbSet stay (used only by the conversion task).

### Task 3: Delete `DatabaseStoreRarFile` + its factory cases

**Files:** Delete `backend/WebDav/DatabaseStoreRarFile.cs`; Modify `DatabaseStoreCollection.cs`, `DatabaseStoreIdFile.cs`, `DatabaseStoreSymlinkCollection.cs`

- [ ] **Step 1: Delete the store**

```bash
git rm backend/WebDav/DatabaseStoreRarFile.cs
```

- [ ] **Step 2: Remove the factory case in `DatabaseStoreCollection.cs`**

Delete lines 116-118 (the `DavItem.ItemType.RarFile => new DatabaseStoreRarFile(...)` arm) so the switch goes straight from `NzbFile` to `MultipartFile`. Also in line 78 change:
```csharp
        if (davItem.Type is DavItem.ItemType.NzbFile or DavItem.ItemType.RarFile or DavItem.ItemType.MultipartFile)
```
to:
```csharp
        if (davItem.Type is DavItem.ItemType.NzbFile or DavItem.ItemType.MultipartFile)
```

- [ ] **Step 3: Remove the factory case in `DatabaseStoreIdFile.cs`**

Delete lines 38-39 (the `DavItem.ItemType.RarFile => new DatabaseStoreRarFile(...)` arm).

- [ ] **Step 4: Remove the case in `DatabaseStoreSymlinkCollection.cs`**

Delete lines 108-109 (`DavItem.ItemType.RarFile => new DatabaseStoreSymlinkFile(davItem, configManager),`). `NzbFile` and `MultipartFile` arms already produce the same `DatabaseStoreSymlinkFile`.

- [ ] **Step 5: Build + commit**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj -v minimal`
Expected: Build succeeded.
```bash
git add -A backend/WebDav/
git commit -m "refactor: remove DatabaseStoreRarFile and its factory branches"
```

### Task 4: Remove remaining legacy serving reads

**Files:** `Database/DavDatabaseClient.cs`, `Services/HealthCheckService.cs`, `Queue/PostProcessors/BlacklistedExtensionPostProcessor.cs`, `Api/Controllers/TestDownload/TestDownloadController.cs`, `Api/Controllers/GetFileDetails/GetFileDetailsController.cs`, `Api/Controllers/ProviderBenchmark/ProviderBenchmarkController.cs`, `Api/Controllers/DownloadNzb/DownloadNzbController.cs`, `Api/Controllers/Maintenance/RepairClassificationController.cs`

- [ ] **Step 1: `DavDatabaseClient.cs:21-23`** — remove the RarFile line:
```csharp
            .Where(i => i.Type == DavItem.ItemType.NzbFile
                        || i.Type == DavItem.ItemType.MultipartFile)
```

- [ ] **Step 2: `HealthCheckService.cs:324-326`** — remove the RarFile line:
```csharp
            .Where(x => (x.Type == DavItem.ItemType.NzbFile
                         || x.Type == DavItem.ItemType.MultipartFile)
                        && x.HistoryItemId == null);
```
And delete the entire `if (davItem.Type == DavItem.ItemType.RarFile) { … }` block at lines 534-541 (the one returning `rarFile?.RarParts?.SelectMany(...)`).

- [ ] **Step 3: `BlacklistedExtensionPostProcessor.cs:35-42`** — delete the whole `else if (davItem.Type == DavItem.ItemType.RarFile) { … }` block (RarFile items are never queued, so this Added-entry branch is dead).

- [ ] **Step 4: `TestDownloadController.cs`** — at lines 44-46 remove the RarFile condition:
```csharp
        if (davItem.Type != DavItem.ItemType.NzbFile &&
            davItem.Type != DavItem.ItemType.MultipartFile)
```
And delete the `case DavItem.ItemType.RarFile:` block (lines 154 through its `return …;`, ending just before the `case DavItem.ItemType.MultipartFile:` or `default:`). Verify the surrounding `switch` still has `NzbFile` and `MultipartFile` cases after removal.

- [ ] **Step 5: `GetFileDetailsController.cs:192-197`** — replace the Rar check:
```csharp
                // Check Multipart
                var isMultipart = await dbClient.Ctx.MultipartFiles.AsNoTracking().AnyAsync(x => x.Id == itemGuid).ConfigureAwait(false);
                if (isMultipart)
                {
                    nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
                }
```
(If a later `else if`/`isMultipart` check already exists below, merge to avoid duplicate work; verify by reading lines 191-205.)

- [ ] **Step 6: `ProviderBenchmarkController.cs`** — remove RAR benchmark support: delete the "Try RAR" block (lines 459-461), the "3. Try RarFiles" candidate block (around 530-552), and the `GetRarFileData` method (around 625 to its closing brace). The NZB and Multipart candidate paths remain.

- [ ] **Step 7: `DownloadNzbController.cs` & `RepairClassificationController.cs`** — stop querying RarFiles: in each, set the `rarFile` local to `null` and remove the `await dbClient.Ctx.RarFiles…` query (lines 77-80 and 88 respectively). Leave the `GenerateNzbXml(… DavRarFile? rarFile …)` signatures and their `rarFile == null ? …` branches unchanged (they reference the still-present model and now always receive null). This keeps the diff minimal; full removal happens in the follow-up.

- [ ] **Step 8: Build + run full tests + commit**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj -v minimal && /opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj -v minimal`
Expected: Build succeeded; tests PASS.
```bash
git add -A backend/
git commit -m "refactor: drop legacy RarFile read paths (model retained for migration only)"
```

> Dev Tools (`Tools/MagicTester.cs`, `Tools/NzbFromDbTester.cs`, `Tools/ExtractTestNzbs.cs`) still reference `RarFiles` for diagnostics. They compile against the retained model and are not part of the served app — leave them; they are removed with the model in the follow-up release.

---

## PHASE 3 — Segment-size streaming fix

### Task 5: Pure `SegmentOffsetTable` + tests; refactor `NzbFileStream`

**Files:** Create `backend/Streams/SegmentOffsetTable.cs`, `backend.Tests/SegmentOffsetTableTests.cs`; Modify `backend/Streams/NzbFileStream.cs:68-85`

- [ ] **Step 1: Write the failing tests**

`backend.Tests/SegmentOffsetTableTests.cs`:
```csharp
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests;

public class SegmentOffsetTableTests
{
    [Fact]
    public void DecodedSizes_SummingToFileSize_BuildOffsets()
    {
        var ok = SegmentOffsetTable.TryBuild(new long[] { 100, 100, 50 }, 3, 250, out var offsets);
        Assert.True(ok);
        Assert.Equal(new long[] { 0, 100, 200, 250 }, offsets);
    }

    [Fact]
    public void EncodedSizes_OverShooting_AreRejected()
    {
        var ok = SegmentOffsetTable.TryBuild(new long[] { 103, 103, 52 }, 3, 250, out var offsets);
        Assert.False(ok);
        Assert.Null(offsets);
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.False(SegmentOffsetTable.TryBuild(null, 3, 250, out var offsets));
        Assert.Null(offsets);
    }

    [Fact]
    public void LengthMismatch_IsRejected()
    {
        Assert.False(SegmentOffsetTable.TryBuild(new long[] { 100, 150 }, 3, 250, out var offsets));
        Assert.Null(offsets);
    }

    [Fact]
    public void NegativeSize_IsRejected()
    {
        Assert.False(SegmentOffsetTable.TryBuild(new long[] { 300, -50, 0 }, 3, 250, out var offsets));
        Assert.Null(offsets);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter SegmentOffsetTableTests -v minimal`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the helper**

`backend/Streams/SegmentOffsetTable.cs`:
```csharp
namespace NzbWebDAV.Streams;

/// <summary>
/// Builds the cumulative byte-offset table used by NzbFileStream for O(log N) seeking.
/// Succeeds only when sizes are well-formed and sum EXACTLY to the expected file size.
/// Approximate or yEnc-encoded (non-decoded) sizes are rejected — callers trust the offsets
/// as ground truth when discarding bytes during a seek, so a wrong offset would silently corrupt output.
/// </summary>
public static class SegmentOffsetTable
{
    public static bool TryBuild(long[]? segmentSizes, int segmentCount, long expectedFileSize, out long[]? offsets)
    {
        offsets = null;
        if (segmentSizes == null || segmentSizes.Length != segmentCount) return false;

        var result = new long[segmentSizes.Length + 1];
        long current = 0;
        for (int i = 0; i < segmentSizes.Length; i++)
        {
            if (segmentSizes[i] < 0) return false;
            result[i] = current;
            current += segmentSizes[i];
        }
        result[^1] = current;

        if (current != expectedFileSize) return false;
        offsets = result;
        return true;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter SegmentOffsetTableTests -v minimal`
Expected: PASS (5 tests).

- [ ] **Step 5: Refactor `NzbFileStream.cs:68-85`** to:
```csharp
        if (segmentSizes != null && segmentSizes.Length == fileSegmentIds.Length)
        {
            if (!SegmentOffsetTable.TryBuild(segmentSizes, fileSegmentIds.Length, _fileSize, out _segmentOffsets))
            {
                Serilog.Log.Warning("[NzbFileStream] Cached segment sizes total {CachedSize} but expected {FileSize}. Ignoring cache.",
                    segmentSizes.Sum(), _fileSize);
            }
        }
```

- [ ] **Step 6: Build + commit**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj -v minimal`
Expected: Build succeeded.
```bash
git add backend/Streams/SegmentOffsetTable.cs backend.Tests/SegmentOffsetTableTests.cs backend/Streams/NzbFileStream.cs
git commit -m "refactor: extract SegmentOffsetTable with exact-sum validation"
```

### Task 6: Add `SegmentSizes` to `DavMultipartFile.FilePart` + serialization tests

**Files:** Modify `backend/Database/Models/DavMultipartFile.cs`; Create `backend.Tests/DavMultipartFileSerializationTests.cs`

- [ ] **Step 1: Write the failing back-compat test**

`backend.Tests/DavMultipartFileSerializationTests.cs`:
```csharp
using System.Text.Json;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests;

public class DavMultipartFileSerializationTests
{
    [Fact]
    public void OldJson_WithoutSegmentSizes_DeserializesWithNull()
    {
        var oldJson = """
        { "AesParams": null, "ObfuscationKey": null, "FileParts": [
          { "SegmentIds": ["a@x","b@x"],
            "SegmentIdByteRange": { "StartInclusive": 0, "EndExclusive": 200 },
            "FilePartByteRange": { "StartInclusive": 10, "EndExclusive": 190 },
            "SegmentFallbacks": null } ] }
        """;
        var meta = JsonSerializer.Deserialize<DavMultipartFile.Meta>(oldJson);
        Assert.NotNull(meta);
        Assert.Null(meta!.FileParts[0].SegmentSizes);
    }

    [Fact]
    public void NewField_RoundTrips()
    {
        var meta = new DavMultipartFile.Meta
        {
            FileParts = new[]
            {
                new DavMultipartFile.FilePart
                {
                    SegmentIds = new[] { "a@x", "b@x" },
                    SegmentIdByteRange = LongRange.FromStartAndSize(0, 200),
                    FilePartByteRange = LongRange.FromStartAndSize(10, 180),
                    SegmentSizes = new long[] { 100, 100 },
                }
            }
        };
        var back = JsonSerializer.Deserialize<DavMultipartFile.Meta>(JsonSerializer.Serialize(meta));
        Assert.Equal(new long[] { 100, 100 }, back!.FileParts[0].SegmentSizes);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter DavMultipartFileSerializationTests -v minimal`
Expected: FAIL to compile.

- [ ] **Step 3: Add the field** to `class FilePart` in `backend/Database/Models/DavMultipartFile.cs` (after `SegmentFallbacks`):
```csharp
        /// <summary>
        /// Decoded byte size of each segment in <see cref="SegmentIds"/> (yEnc PartSize).
        /// When present and summing exactly to <see cref="SegmentIdByteRange"/>.Count, enables O(log N)
        /// seeking in NzbFileStream. Null for items created before this field existed or not yet populated;
        /// populated lazily on first stream (DatabaseStoreMultipartFile) and eagerly during RAR processing.
        /// </summary>
        public long[]? SegmentSizes { get; set; }
```

- [ ] **Step 4: Run to verify it passes**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter DavMultipartFileSerializationTests -v minimal`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add backend/Database/Models/DavMultipartFile.cs backend.Tests/DavMultipartFileSerializationTests.cs
git commit -m "feat: add SegmentSizes to DavMultipartFile.FilePart (JSON column; no migration)"
```

### Task 7: `DavMultipartFileStream` passes `SegmentSizes`

**Files:** Modify `backend/Streams/DavMultipartFileStream.cs:101-108`

- [ ] **Step 1: Pass the sizes** — change the `usenet.GetFileStream(...)` call inside `StreamFactory` to:
```csharp
                    var stream = usenet.GetFileStream(
                        capturedPart.SegmentIds,
                        capturedPart.SegmentIdByteRange.Count,
                        concurrentConnections,
                        partContext,
                        useBufferedStreaming: true,
                        segmentSizes: capturedPart.SegmentSizes,
                        segmentFallbacks: capturedPart.SegmentFallbacks
                    );
```

- [ ] **Step 2: Build + commit**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj -v minimal`
Expected: Build succeeded. (Null `SegmentSizes` = today's behavior; wrong sizes are rejected by `SegmentOffsetTable`.)
```bash
git add backend/Streams/DavMultipartFileStream.cs
git commit -m "feat: DavMultipartFileStream passes per-part SegmentSizes to NzbFileStream"
```

### Task 8: Lazy compute + validate + persist in `DatabaseStoreMultipartFile`

**Files:** Create `backend/WebDav/SegmentSizePopulation.cs`, `backend.Tests/SegmentSizePopulationTests.cs`; Modify `backend/WebDav/DatabaseStoreMultipartFile.cs`

- [ ] **Step 1: Write the failing unit tests**

`backend.Tests/SegmentSizePopulationTests.cs`:
```csharp
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Tests;

public class SegmentSizePopulationTests
{
    private static DavMultipartFile.FilePart Part(long[]? sizes) => new()
    {
        SegmentIds = new[] { "a@x", "b@x" },
        SegmentIdByteRange = LongRange.FromStartAndSize(0, 200),
        FilePartByteRange = LongRange.FromStartAndSize(0, 200),
        SegmentSizes = sizes,
    };

    [Fact]
    public void NeedsPopulation_TrueWhenAnyPartNull()
    {
        var meta = new DavMultipartFile.Meta { FileParts = new[] { Part(new long[] { 100, 100 }), Part(null) } };
        Assert.True(SegmentSizePopulation.NeedsPopulation(meta));
    }

    [Fact]
    public void NeedsPopulation_FalseWhenAllPresent()
    {
        var meta = new DavMultipartFile.Meta { FileParts = new[] { Part(new long[] { 100, 100 }) } };
        Assert.False(SegmentSizePopulation.NeedsPopulation(meta));
    }

    [Fact]
    public void IsValidForPart_TrueWhenSumsToPartSize()
        => Assert.True(SegmentSizePopulation.IsValidForPart(Part(null), new long[] { 100, 100 }));

    [Fact]
    public void IsValidForPart_FalseWhenSumWrong_OrCountWrong()
    {
        Assert.False(SegmentSizePopulation.IsValidForPart(Part(null), new long[] { 103, 103 }));
        Assert.False(SegmentSizePopulation.IsValidForPart(Part(null), new long[] { 200 }));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter SegmentSizePopulationTests -v minimal`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the helper** — `backend/WebDav/SegmentSizePopulation.cs`:
```csharp
using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.WebDav;

/// <summary>Pure decide/validate logic for lazily populating DavMultipartFile.FilePart.SegmentSizes.</summary>
public static class SegmentSizePopulation
{
    public static bool NeedsPopulation(DavMultipartFile.Meta meta) =>
        meta.FileParts.Any(p => p.SegmentSizes == null || p.SegmentSizes.Length != p.SegmentIds.Length);

    public static bool IsValidForPart(DavMultipartFile.FilePart part, long[] computedSizes) =>
        SegmentOffsetTable.TryBuild(computedSizes, part.SegmentIds.Length, part.SegmentIdByteRange.Count, out _);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj --filter SegmentSizePopulationTests -v minimal`
Expected: PASS (4 tests).

- [ ] **Step 5: Wire lazy population into `DatabaseStoreMultipartFile.GetStreamAsync`** — after the `if (multipartFile is null) …` guard (line 57) and before constructing `packedStream` (line 58), insert:
```csharp
        if (SegmentSizePopulation.NeedsPopulation(multipartFile.Metadata))
        {
            var changed = false;
            foreach (var part in multipartFile.Metadata.FileParts)
            {
                if (part.SegmentSizes != null && part.SegmentSizes.Length == part.SegmentIds.Length) continue;
                if (part.SegmentIds.Length == 0) continue;
                try
                {
                    var sizes = await usenetClient.AnalyzeNzbAsync(
                        part.SegmentIds, configManager.GetTotalStreamingConnections(),
                        progress: null, ct, useSmartAnalysis: true).ConfigureAwait(false);

                    if (SegmentSizePopulation.IsValidForPart(part, sizes)) { part.SegmentSizes = sizes; changed = true; }
                    else Serilog.Log.Warning("[DatabaseStoreMultipartFile] Computed sizes for '{File}' did not sum to part size; will interpolate.", davMultipartFile.Name);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[DatabaseStoreMultipartFile] Failed to compute segment sizes for '{File}'; will interpolate.", davMultipartFile.Name);
                }
            }

            if (changed)
            {
                dbClient.Ctx.MultipartFiles.Update(multipartFile);
                dbClient.Ctx.Entry(multipartFile).Property(x => x.Metadata).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                Serilog.Log.Information("[DatabaseStoreMultipartFile] Persisted segment sizes for '{File}'.", davMultipartFile.Name);
            }
        }
```

- [ ] **Step 6: Build + full tests + commit**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj -v minimal && /opt/homebrew/opt/dotnet/bin/dotnet test backend.Tests/NzbWebDAV.Tests.csproj -v minimal`
Expected: Build succeeded; all tests PASS.
```bash
git add backend/WebDav/SegmentSizePopulation.cs backend.Tests/SegmentSizePopulationTests.cs backend/WebDav/DatabaseStoreMultipartFile.cs
git commit -m "feat: lazily compute+persist exact SegmentSizes on first multipart stream"
```

### Task 9 (recommended): Eager population during RAR processing

Avoids the one-time first-play latency for newly imported RAR content. Lazy population already makes everything correct; this is latency-only.

**Files:** Modify `backend/Queue/FileProcessors/RarProcessor.cs`, `backend/Queue/FileAggregators/RarAggregator.cs`

- [ ] **Step 1:** In `RarProcessor.cs`, add to `class StoredFileSegment` (after `ByteRangeWithinPart`):
```csharp
        public long[]? SegmentSizes { get; init; }
```

- [ ] **Step 2:** Where each part's `StoredFileSegment` is constructed (near `PartSize = stream.Length`, `:147`), compute decoded sizes first. Add `using NzbWebDAV.Streams;` to the file, then before the `new StoredFileSegment { … }`:
```csharp
                long[]? partSegmentSizes = null;
                try
                {
                    var partIds = x.NzbFile.GetSegmentIds();
                    if (partIds.Length > 0)
                    {
                        var computed = await usenet.AnalyzeNzbAsync(partIds, RarHeaderConnections, null, ct, useSmartAnalysis: true).ConfigureAwait(false);
                        if (SegmentOffsetTable.TryBuild(computed, partIds.Length, stream.Length, out _))
                            partSegmentSizes = computed;
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "[RarProcessor] Could not precompute segment sizes for part; lazy path will handle it.");
                }
```
and set `SegmentSizes = partSegmentSizes,` in the `StoredFileSegment { … }` initializer.
> Implementer note: confirm `x.NzbFile` and `stream.Length` are in scope at that construction site (read 130-160). If the loop variable differs, adjust names accordingly.

- [ ] **Step 3:** In `RarAggregator.cs:82-88`, add to the `new DavMultipartFile.FilePart() { … }` initializer:
```csharp
                            SegmentSizes = x.SegmentSizes,
```

- [ ] **Step 4: Build + commit**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj -v minimal`
Expected: Build succeeded.
```bash
git add backend/Queue/FileProcessors/RarProcessor.cs backend/Queue/FileAggregators/RarAggregator.cs
git commit -m "perf: eagerly populate SegmentSizes during RAR processing"
```

---

## PHASE 4 — Release & verification

### Task 10: Release chores + live verification

**Files:** `VERSION`, `README.md`, `backend/Program.cs`

- [ ] **Step 1: Bump `VERSION`** to:
```
0.8.0
```

- [ ] **Step 2: Add changelog at top of `## Changelog` in `README.md`:**
```markdown
## v0.8.0 (2026-05-25)
- **Fix:** RAR/multipart streams now seek precisely. Per-segment decoded sizes (`SegmentSizes`) are stored on `DavMultipartFile.FilePart` and passed to `NzbFileStream`, eliminating slow per-seek yEnc-header interpolation that caused Stremio tail-probe timeouts and `Content-Length mismatch: too few bytes written (0 of …)` on RAR-packed releases. Stored in the existing JSON column (no DB migration); existing items self-heal on first play, new items are populated at import. Size arrays that don't sum exactly to the part size are rejected, so wrong bytes are never served.
- **Refactor:** Legacy `DavRarFile` items are migrated to `DavMultipartFile` on startup (one-time, idempotent) and the legacy serving code is removed, leaving a single multipart streaming model. The `DavRarFile` table/model is retained this release for the migration and will be dropped in a follow-up.
```

- [ ] **Step 3: Update the `BUILD v` string in `backend/Program.cs`** (search `BUILD v`) to:
```
BUILD v2026-05-25-RAR-SEGMENT-OFFSETS
```

- [ ] **Step 4: Commit, push test branch, deploy, verify**

```bash
git add VERSION README.md backend/Program.cs
git commit -m "chore: v0.8.0 — exact RAR/multipart segment offsets + legacy RarFile migration"
git push origin HEAD:rar-segment-size-fix
```
After CI builds the image, deploy the test image to the `nzbdav2` container on the NAS.

**Migration check (one-time):**
- On first startup, logs show `[LegacyRarFileMigration] Converting N legacy DavRarFile rows…` then `Converted N`. Second restart logs nothing (idempotent).
- `sudo /usr/local/bin/docker exec nzbdav2 sqlite3 <db-path> "SELECT COUNT(*) FROM DavRarFiles;"` returns `0` after first run. (Find the DB path from the container config.)

**Streaming check** — play each previously-failing title (Would I Lie To You, Last Week Tonight, Dating Naked) and inspect `sudo /usr/local/bin/docker logs nzbdav2` and `… usenetstreamer`:
- No `[NzbFileStream] Cached segment sizes total … Ignoring cache.` on the second play.
- No `System.InvalidOperationException: Response Content-Length mismatch: too few bytes written (0 of …)`.
- No `[NZBDAV] Probe prefetch failed … timeout of 60000ms exceeded` on the UsenetStreamer side.
- First play logs `Persisted segment sizes for '<title>'`; second play has no population log and starts fast.
- All three play to the point upstream `nzbdav`/`usenetstreamer2` reach.

- [ ] **Step 5: Promote to master after the user confirms**

```bash
git checkout master && git merge --ff-only rar-segment-size-fix && git push origin master
```

---

## Follow-up release (out of scope — track separately)

After this release has been deployed everywhere and `SELECT COUNT(*) FROM DavRarFiles` is 0 on all instances:
1. Add an EF migration dropping the `DavRarFiles` table.
2. Delete `Database/Models/DavRarFile.cs`, the `RarFiles` DbSet + JSON mapping in `DavDatabaseContext.cs`, `LegacyRarFileMigration.cs` + its `Program.cs` call, the `DavRarFile?` params in `DownloadNzbController`/`RepairClassificationController` `GenerateNzbXml`, and the `RarFile` usages in `Tools/*`.
3. Optionally remove the `ItemType.RarFile = 4` enum value (safe once no rows use it).

---

## Self-Review

**1. Spec coverage** — Migrate legacy rows: Task 2. Remove legacy serving: Tasks 3-4. Exact decoded offsets (guarantee): Task 5 + reused in Tasks 8/9. Persist on `DavMultipartFile.FilePart`, no migration: Task 6. Stream pass-through: Task 7. Lazy backfill (existing + converted items): Task 8. Eager (new items): Task 9. Staged deletion: Phase-1 keeps model, Follow-up section drops it. Release rules (VERSION/README/BUILD): Task 10. ✓

**2. Placeholder scan** — No TBD/"handle edge cases". Implementer notes (DbContext options constructor in Task 2; loop-variable confirmation in Task 9; DB path in Task 10) are concrete verification instructions with fallbacks, not placeholders.

**3. Type consistency** — `SegmentOffsetTable.TryBuild(long[]?, int, long, out long[]?)` identical in Tasks 5/8/9. `DavMultipartFile.FilePart.SegmentSizes` (`long[]?`) defined Task 6, used Tasks 7/8/9. `SegmentSizePopulation.NeedsPopulation/IsValidForPart` defined+used Task 8. `LegacyRarFileMigration.RunAsync(DavDatabaseContext, CancellationToken)` defined Task 2, called Task 2 Step 5. `AnalyzeNzbAsync(string[], int, IProgress<int>?, CancellationToken, bool)` matches source. `GetFileStream(…, segmentSizes:, segmentFallbacks:)` matches source.

**Known limitation (by design):** Non-uniform parts make `AnalyzeNzbAsync` do a full N-fetch scan, bounded by the usenet operation timeout; on failure the code leaves sizes null and interpolates (today's behavior) — never blocks or corrupts.
