# History-Aware Cleanup Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add HistoryItemId tracking to DavItems so orphan cleanup and directory deletion are history-aware — items still linked to active history entries are protected from cleanup.

**Architecture:** Add `HistoryItemId` nullable FK column to DavItems, populated when mount folders/files are created during queue processing. Replace cascade FK delete on DavItems parent→child with async cleanup queues (`HistoryCleanupItems`, `DavCleanupItems`) processed by two new BackgroundServices. Refactor `RemoveUnlinkedFilesTask` to use raw SQL with `HistoryItemId IS NULL` filter and batched deletion.

**Tech Stack:** C# / .NET 10, EF Core 10, SQLite

---

## Key Design Decisions

**Not adopting from upstream:**
- `ItemSubType` enum / `SubType` column — upstream uses this to distinguish directory types (Directory vs WebdavRoot, NzbsRoot, etc.) and file types (NzbFile, RarFile, MultipartFile). Our fork keeps the existing `ItemType` enum which already separates these (Directory=1, SymlinkRoot=2, NzbFile=3, RarFile=4, IdsRoot=5, MultipartFile=6). We use `IsProtected()` for root folder detection instead.
- `ItemType.UsenetFile` consolidation — upstream merged NzbFile/RarFile/MultipartFile into one `UsenetFile` type + SubType. Our fork keeps them separate.
- `DavCleanupItems` table / `DavCleanupService` / `TR_DavItems_DeleteDirectory` trigger — these replace cascade delete. Our fork keeps cascade delete since we don't have the blobstore triggers that required upstream to drop cascade (SQLite rebuilds tables when dropping FKs, destroying all triggers). Without blobstore triggers to protect, cascade delete is simpler and correct.
- `RcloneVfsForget` calls in cleanup services — will be added in item 10 (rclone vfs/forget integration).
- `await using` on `new DavDatabaseContext()` — upstream creates standalone contexts in tasks/services. Our fork uses DI-scoped contexts via `IServiceScopeFactory`. We'll keep our pattern.

**Adopting from upstream:**
- `HistoryItemId` nullable column on DavItems (no FK constraint, just a tracking column)
- `HistoryCleanupItems` table + `HistoryCleanupService` BackgroundService
- `HistoryItemId` populated on mount folders and child DavItems during queue processing
- `RemoveHistoryItemsAsync` inserts `HistoryCleanupItem` entries instead of directly deleting DavItems
- `RemoveUnlinkedFilesTask` rewritten: raw SQL, temp table for linked IDs, `HistoryItemId IS NULL` filter, batched deletion
- `RemoveFromHistoryController` simplified: no explicit transaction wrapper

---

## Task 1: EF Migration — Add HistoryItemId and HistoryCleanupItems

**Files:**
- Create: `backend/Database/Models/HistoryCleanupItem.cs`
- Modify: `backend/Database/Models/DavItem.cs`
- Modify: `backend/Database/DavDatabaseContext.cs:29,239-252`
- Create: `backend/Database/Migrations/<timestamp>_AddHistoryCleanup.cs` (auto-generated)

- [ ] **Step 1: Create HistoryCleanupItem model**

Create `backend/Database/Models/HistoryCleanupItem.cs`:

```csharp
namespace NzbWebDAV.Database.Models;

public class HistoryCleanupItem
{
    public Guid Id { get; set; }
    public bool DeleteMountedFiles { get; set; }
}
```

- [ ] **Step 2: Add HistoryItemId to DavItem model**

In `backend/Database/Models/DavItem.cs`, add after `CorruptionReason`:

```csharp
public Guid? HistoryItemId { get; set; }
```

Update the `DavItem.New` factory method signature to accept `Guid? historyItemId` and set it.

- [ ] **Step 3: Update DavDatabaseContext**

In `backend/Database/DavDatabaseContext.cs`:

Add DbSet:
```csharp
public DbSet<HistoryCleanupItem> HistoryCleanupItems => Set<HistoryCleanupItem>();
```

In `OnModelCreating`, update the DavItem entity config:

After the `IsCorrupted` property config (line ~237), add:
```csharp
e.Property(i => i.HistoryItemId)
    .ValueGeneratedNever()
    .IsRequired(false);
```

Replace the existing 4-column index:
```csharp
// OLD: e.HasIndex(i => new { i.Type, i.NextHealthCheck, i.ReleaseDate, i.Id });
// NEW:
e.HasIndex(i => new { i.Type, i.HistoryItemId, i.NextHealthCheck, i.ReleaseDate, i.Id });
```

Add new index:
```csharp
e.HasIndex(i => new { i.HistoryItemId, i.Type, i.CreatedAt });
```

Add HistoryCleanupItem entity config:
```csharp
// HistoryCleanupItem
b.Entity<HistoryCleanupItem>(e =>
{
    e.ToTable("HistoryCleanupItems");
    e.HasKey(i => i.Id);
    e.Property(i => i.Id).ValueGeneratedNever();
    e.Property(i => i.DeleteMountedFiles).IsRequired();
});
```

Keep the existing cascade delete FK on DavItems parent→child (line 239-242). Do NOT remove it.

- [ ] **Step 4: Generate EF migration**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet ef migrations add AddHistoryCleanup
```

- [ ] **Step 5: Build and verify migration compiles**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet build
```

- [ ] **Step 6: Commit**

```bash
git add backend/Database/Models/HistoryCleanupItem.cs backend/Database/Models/DavItem.cs backend/Database/DavDatabaseContext.cs backend/Database/Migrations/
git commit -m "feat: add HistoryItemId to DavItems and HistoryCleanupItems table"
```

---

## Task 2: Populate HistoryItemId During Queue Processing

**Files:**
- Modify: `backend/Database/Models/DavItem.cs:25-51` (factory method)
- Modify: `backend/Queue/QueueItemProcessor.cs:459-514` (category/mount folder creation)
- Modify: `backend/Queue/FileAggregators/BaseAggregator.cs:35-43` (directory creation)
- Modify: `backend/Queue/FileAggregators/FileAggregator.cs` (file DavItem creation)
- Modify: `backend/Queue/FileAggregators/RarAggregator.cs` (file DavItem creation)
- Modify: `backend/Queue/FileAggregators/MultipartMkvAggregator.cs` (file DavItem creation)
- Modify: `backend/Queue/FileAggregators/SevenZipAggregator.cs` (file DavItem creation)

- [ ] **Step 1: Update DavItem.New signature**

In `backend/Database/Models/DavItem.cs`, update `DavItem.New`:

```csharp
public static DavItem New
(
    Guid id,
    DavItem parent,
    string name,
    long? fileSize,
    ItemType type,
    DateTimeOffset? releaseDate,
    DateTimeOffset? lastHealthCheck,
    Guid? historyItemId = null
)
{
    return new DavItem()
    {
        Id = id,
        IdPrefix = id.GetFiveLengthPrefix(),
        CreatedAt = DateTime.Now,
        ParentId = parent.Id,
        Name = name,
        FileSize = fileSize,
        Type = type,
        Path = System.IO.Path.Join(parent.Path, name),
        ReleaseDate = releaseDate,
        LastHealthCheck = lastHealthCheck,
        NextHealthCheck = releaseDate != null && lastHealthCheck != null
            ? releaseDate.Value + 2 * (lastHealthCheck.Value - releaseDate.Value)
            : null,
        HistoryItemId = historyItemId
    };
}
```

The `historyItemId` parameter defaults to `null` so existing callers that don't pass it (e.g., ContentSnapshotService) continue working without changes.

- [ ] **Step 2: Pass historyItemId in QueueItemProcessor**

In `backend/Queue/QueueItemProcessor.cs`:

`CreateMountFolder` (~line 483): pass `historyItemId: queueItem.Id`:
```csharp
var mountFolder = DavItem.New(
    id: GuidUtil.CreateDeterministic(categoryFolder.Id, queueItem.JobName),
    parent: categoryFolder,
    name: queueItem.JobName,
    fileSize: null,
    type: DavItem.ItemType.Directory,
    releaseDate: null,
    lastHealthCheck: null,
    historyItemId: queueItem.Id
);
```

`IncrementMountFolder` (~line 504): same — pass `historyItemId: queueItem.Id`:
```csharp
var mountFolder = DavItem.New(
    id: GuidUtil.CreateDeterministic(categoryFolder.Id, name),
    parent: categoryFolder,
    name: name,
    fileSize: null,
    type: DavItem.ItemType.Directory,
    releaseDate: null,
    lastHealthCheck: null,
    historyItemId: queueItem.Id
);
```

Category folders (`GetOrCreateCategoryFolder`, ~line 459): do NOT pass `historyItemId` — category folders are shared across multiple history items.

- [ ] **Step 3: Pass historyItemId in BaseAggregator**

In `backend/Queue/FileAggregators/BaseAggregator.cs`, update `EnsureDirectory`:

```csharp
var directory = DavItem.New(
    id: Guid.NewGuid(),
    parent: parentDirectory,
    name: directoryName,
    fileSize: null,
    type: DavItem.ItemType.Directory,
    releaseDate: null,
    lastHealthCheck: null,
    historyItemId: MountDirectory.HistoryItemId
);
```

This propagates the mount folder's `HistoryItemId` to subdirectories within the mount.

- [ ] **Step 4: Pass historyItemId in file aggregators**

In each file aggregator's `DavItem.New` call, add `historyItemId: MountDirectory.HistoryItemId`:

- `backend/Queue/FileAggregators/FileAggregator.cs`
- `backend/Queue/FileAggregators/RarAggregator.cs`
- `backend/Queue/FileAggregators/MultipartMkvAggregator.cs`
- `backend/Queue/FileAggregators/SevenZipAggregator.cs`

Read each file first — find the `DavItem.New(` call and add the parameter.

- [ ] **Step 5: Build and verify**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet build
```

- [ ] **Step 6: Commit**

```bash
git add backend/Database/Models/DavItem.cs backend/Queue/
git commit -m "feat: populate HistoryItemId on DavItems during queue processing"
```

---

## Task 3: HistoryCleanupService

**Files:**
- Create: `backend/Services/HistoryCleanupService.cs`
- Modify: `backend/Program.cs` (register hosted service)

- [ ] **Step 1: Create HistoryCleanupService**

Create `backend/Services/HistoryCleanupService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class HistoryCleanupService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                var cleanupItem = await dbContext.HistoryCleanupItems
                    .FirstOrDefaultAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (cleanupItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (cleanupItem.DeleteMountedFiles)
                {
                    var deleted = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteDeleteAsync(stoppingToken)
                        .ConfigureAwait(false);

                    Log.Information("[HistoryCleanup] Deleted {Count} DavItems for history item {Id}",
                        deleted, cleanupItem.Id);
                }
                else
                {
                    var updated = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteUpdateAsync(
                            x => x.SetProperty(p => p.HistoryItemId, (Guid?)null),
                            stoppingToken
                        ).ConfigureAwait(false);

                    Log.Debug("[HistoryCleanup] Unlinked {Count} DavItems from history item {Id}",
                        updated, cleanupItem.Id);
                }

                dbContext.HistoryCleanupItems.Remove(cleanupItem);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, "[HistoryCleanup] Error processing cleanup queue: {Message}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
```

- [ ] **Step 2: Register in Program.cs**

In `backend/Program.cs`, add after the `DatabaseMaintenanceService` registration:

```csharp
builder.Services.AddHostedService<HistoryCleanupService>();
```

- [ ] **Step 3: Build and verify**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add backend/Services/HistoryCleanupService.cs backend/Program.cs
git commit -m "feat: add HistoryCleanupService background worker"
```

---

## Task 4: Update RemoveHistoryItemsAsync + RemoveFromHistoryController

**Files:**
- Modify: `backend/Database/DavDatabaseClient.cs:199-232`
- Modify: `backend/Api/SabControllers/RemoveFromHistory/RemoveFromHistoryController.cs:62-81`

- [ ] **Step 1: Rewrite RemoveHistoryItemsAsync**

In `backend/Database/DavDatabaseClient.cs`, replace the current `RemoveHistoryItemsAsync`:

```csharp
public async Task RemoveHistoryItemsAsync(List<Guid> ids, bool deleteFiles, CancellationToken ct = default)
{
    var historyItems = await Ctx.HistoryItems
        .Where(x => ids.Contains(x.Id))
        .ToListAsync(ct).ConfigureAwait(false);

    if (historyItems.Count == 0)
    {
        Serilog.Log.Debug("[DavDatabaseClient] RemoveHistoryItemsAsync: No history items found for ids: {Ids}", string.Join(",", ids));
        return;
    }

    Serilog.Log.Information("[DavDatabaseClient] Removing {Count} history items: {Names}",
        historyItems.Count, string.Join(", ", historyItems.Select(h => h.JobName)));

    // Queue cleanup items for HistoryCleanupService to process
    Ctx.HistoryCleanupItems.AddRange(historyItems.Select(x => new Database.Models.HistoryCleanupItem
    {
        Id = x.Id,
        DeleteMountedFiles = deleteFiles
    }));

    // Remove history items
    Ctx.HistoryItems.RemoveRange(historyItems);
}
```

This replaces the direct `ExecuteDeleteAsync` on DavItems with queued cleanup via `HistoryCleanupService`.

- [ ] **Step 2: Simplify RemoveFromHistoryController**

In `backend/Api/SabControllers/RemoveFromHistory/RemoveFromHistoryController.cs`, simplify the UI deletion path (remove explicit transaction):

Replace lines 68-72:
```csharp
// OLD:
await using var transaction = await dbClient.Ctx.Database.BeginTransactionAsync().ConfigureAwait(false);
await dbClient.RemoveHistoryItemsAsync(request.NzoIds, request.DeleteCompletedFiles, request.CancellationToken).ConfigureAwait(false);
await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
await transaction.CommitAsync(request.CancellationToken).ConfigureAwait(false);
```

With:
```csharp
// NEW:
await dbClient.RemoveHistoryItemsAsync(request.NzoIds, request.DeleteCompletedFiles, request.CancellationToken).ConfigureAwait(false);
await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
```

The transaction is no longer needed since DavItem deletion is now async (via HistoryCleanupService) rather than in the same transaction.

- [ ] **Step 3: Build and verify**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add backend/Database/DavDatabaseClient.cs backend/Api/SabControllers/RemoveFromHistory/
git commit -m "feat: queue history cleanup instead of direct DavItem deletion"
```

---

## Task 5: Rewrite RemoveUnlinkedFilesTask

**Files:**
- Modify: `backend/Tasks/RemoveUnlinkedFilesTask.cs` (full rewrite)

- [ ] **Step 1: Rewrite RemoveUnlinkedFilesTask**

Replace the entire `backend/Tasks/RemoveUnlinkedFilesTask.cs` with the history-aware version.

Key changes from current implementation:
1. **Constructor**: Remove `DavDatabaseClient` and `ProviderErrorService` dependencies. Use `IServiceScopeFactory` for DB access.
2. **Temp table**: Write linked IDs to `TMP_LINKED_FILES` table instead of loading all DavItems into memory.
3. **HistoryItemId IS NULL filter**: All unlinked item queries include this filter.
4. **Raw SQL deletion**: Delete in batches of 100 via raw SQL instead of EF tracking.
5. **Empty directory cleanup**: Uses separate pass with `LEFT JOIN` to find empty directories. Only targets `ItemType.Directory` (value=1) and excludes protected folders by ID.
6. **Dry run**: Separate code path that only queries, doesn't delete.

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class RemoveUnlinkedFilesTask(
    ConfigManager configManager,
    IServiceScopeFactory scopeFactory,
    WebsocketManager websocketManager,
    bool isDryRun
) : BaseTask
{
    private static List<string> _allRemovedPaths = [];

    private record UnlinkedItemInfo(string Id, int Type, string Path);

    protected override async Task ExecuteInternal()
    {
        try
        {
            await RemoveUnlinkedFiles().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to remove unlinked files.");
        }
    }

    private async Task RemoveUnlinkedFiles()
    {
        Report("Scanning all linked files...");
        var startTime = DateTime.Now;
        var linkedIdCount = await WriteLinkedIdsToTable();
        if (linkedIdCount < 5)
        {
            Report("Aborted: " +
                   "There are less than five linked files found in your library. " +
                   "Cancelling operation to prevent accidental bulk deletion.");
            return;
        }

        Report("Searching for unlinked webdav items...");
        var unlinkedItems = await CountUnlinkedItems(startTime);
        Report($"Found {unlinkedItems} webdav items to remove.");

        if (isDryRun)
        {
            await DryRunIdentifyUnlinkedFiles(startTime);
            Report($"Done. Identified {_allRemovedPaths.Count} unlinked files.");
        }
        else
        {
            await RemoveUnlinkedItems(startTime, unlinkedItems);
            await RemoveEmptyDirectories(startTime);
            Report($"Done. Removed {_allRemovedPaths.Count} unlinked files.");
        }
    }

    private async Task<int> WriteLinkedIdsToTable()
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS TMP_LINKED_FILES;
            CREATE TABLE TMP_LINKED_FILES (Id TEXT NOT NULL);
            """);

        var count = 0;
        var batches = GetLinkedIds().ToBatches(100);
        foreach (var batch in batches)
        {
            var values = string.Join(",", batch.Select(id => $"('{id.ToString().ToUpper()}')"));
            await dbContext.Database.ExecuteSqlRawAsync(
                $"INSERT INTO TMP_LINKED_FILES (Id) VALUES {values}");
            count += batch.Count;
        }

        Report($"Indexing {count} linked files...");
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE TMP_LINKED_FILES_UNIQUE (Id TEXT NOT NULL PRIMARY KEY);
            INSERT OR IGNORE INTO TMP_LINKED_FILES_UNIQUE (Id) SELECT Id FROM TMP_LINKED_FILES;
            DROP TABLE TMP_LINKED_FILES;
            ALTER TABLE TMP_LINKED_FILES_UNIQUE RENAME TO TMP_LINKED_FILES;
            """);

        return count;
    }

    private IEnumerable<Guid> GetLinkedIds()
    {
        return OrganizedLinksUtil
            .GetLibraryDavItemLinks(configManager)
            .Select(x => x.DavItemId);
    }

    private async Task<int> CountUnlinkedItems(DateTime createdBefore)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var createdBeforeStr = createdBefore.ToString("yyyy-MM-dd HH:mm:ss");

        // File types: NzbFile=3, RarFile=4, MultipartFile=6
        var count = await dbContext.Database
            .SqlQueryRaw<int>(
                $"""
                 SELECT COUNT(i.Id) AS Value FROM DavItems i
                 LEFT JOIN TMP_LINKED_FILES t ON i.Id = t.Id
                 WHERE i.Type IN (3, 4, 6)
                   AND i.HistoryItemId IS NULL
                   AND i.CreatedAt < '{createdBeforeStr}'
                   AND t.Id IS NULL
                 """)
            .FirstAsync();

        return count;
    }

    private async Task RemoveUnlinkedItems(DateTime createdBefore, int totalCount)
    {
        Report("Removing unlinked items...");
        _allRemovedPaths.Clear();
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var removed = 0;

        while (true)
        {
            var itemsToDelete = await dbContext.Database
                .SqlQueryRaw<UnlinkedItemInfo>(
                    $"""
                     SELECT Id, Type, Path FROM DavItems
                     WHERE Type IN (3, 4, 6)
                       AND HistoryItemId IS NULL
                       AND CreatedAt < '{createdBefore:yyyy-MM-dd HH:mm:ss}'
                       AND Id NOT IN (SELECT Id FROM TMP_LINKED_FILES)
                     LIMIT 100
                     """)
                .ToListAsync();

            if (itemsToDelete.Count == 0)
                break;

            var idsToDelete = string.Join(",", itemsToDelete.Select(x => $"'{x.Id}'"));
            await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM DavItems WHERE Id IN ({idsToDelete})");

            _allRemovedPaths.AddRange(itemsToDelete.Select(x => x.Path));
            removed += itemsToDelete.Count;
            Report($"Removing unlinked items...\nRemoved {removed}/{totalCount}...");
        }
    }

    private async Task RemoveEmptyDirectories(DateTime createdBefore)
    {
        Report("Removing empty directories...");
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var removed = 0;

        // Protected folder IDs to exclude
        var protectedIds = new[]
        {
            DavItem.Root.Id, DavItem.NzbFolder.Id, DavItem.ContentFolder.Id,
            DavItem.SymlinkFolder.Id, DavItem.IdsFolder.Id
        };
        var protectedIdStr = string.Join(",", protectedIds.Select(id => $"'{id.ToString().ToUpper()}'"));

        while (true)
        {
            var emptyDirs = await dbContext.Database
                .SqlQueryRaw<UnlinkedItemInfo>(
                    $"""
                     SELECT d.Id, d.Type, d.Path FROM DavItems d
                     LEFT JOIN DavItems c ON c.ParentId = d.Id
                     WHERE d.Type = {(int)DavItem.ItemType.Directory}
                       AND d.CreatedAt < '{createdBefore:yyyy-MM-dd HH:mm:ss}'
                       AND d.Id NOT IN ({protectedIdStr})
                       AND c.Id IS NULL
                     LIMIT 100
                     """)
                .ToListAsync();

            if (emptyDirs.Count == 0)
                break;

            var idsToDelete = string.Join(",", emptyDirs.Select(x => $"'{x.Id}'"));
            await dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM DavItems WHERE Id IN ({idsToDelete})");

            _allRemovedPaths.AddRange(emptyDirs.Select(x => x.Path));
            removed += emptyDirs.Count;
            Report($"Removing empty directories...\nRemoved {removed}...");
        }
    }

    private async Task DryRunIdentifyUnlinkedFiles(DateTime createdBefore)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        var unlinkedFiles = await dbContext.Database
            .SqlQueryRaw<UnlinkedItemInfo>(
                $"""
                 SELECT Id, Type, Path FROM DavItems
                 WHERE Type IN (3, 4, 6)
                   AND HistoryItemId IS NULL
                   AND CreatedAt < '{createdBefore:yyyy-MM-dd HH:mm:ss}'
                   AND Id NOT IN (SELECT Id FROM TMP_LINKED_FILES)
                 """)
            .ToListAsync();

        _allRemovedPaths = unlinkedFiles.Select(x => x.Path).ToList();
    }

    private void Report(string message)
    {
        var dryRun = isDryRun ? "Dry Run - " : string.Empty;
        _ = websocketManager.SendMessage(WebsocketTopic.CleanupTaskProgress, $"{dryRun}{message}");
    }

    public static string GetAuditReport()
    {
        return _allRemovedPaths.Count > 0
            ? string.Join("\n", _allRemovedPaths)
            : "This list is Empty.\nYou must first run the task.";
    }
}
```

- [ ] **Step 2: Check ToBatches extension exists**

Verify `ToBatches` extension method exists in our codebase. If not, check what `IEnumerable` chunking method we have available (could use LINQ `.Chunk(100)` on .NET 10).

```bash
grep -r "ToBatches\|Chunk\|Batch" backend/Extensions/ backend/Utils/
```

- [ ] **Step 3: Update RemoveUnlinkedFilesTask callers**

Find where `RemoveUnlinkedFilesTask` is instantiated and update the constructor call — replace `DavDatabaseClient dbClient` and `ProviderErrorService providerErrorService` with `IServiceScopeFactory scopeFactory`.

```bash
grep -rn "RemoveUnlinkedFilesTask" backend/
```

Update each instantiation site.

- [ ] **Step 4: Build and verify**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add backend/Tasks/RemoveUnlinkedFilesTask.cs backend/
git commit -m "feat: rewrite RemoveUnlinkedFilesTask with history-aware orphan protection"
```

---

## Task 6: Update HealthCheckService to use HistoryItemId

**Files:**
- Modify: `backend/Services/HealthCheckService.cs:308-335`

- [ ] **Step 1: Simplify health check filter**

In `backend/Services/HealthCheckService.cs`, the `GetHealthCheckQueueItemsQuery` method currently uses a subquery on `HistoryItems` to find pending history entries. Now that DavItems have `HistoryItemId`, we can simplify:

Replace the `pendingHistoryDirIds` subquery + filter with:
```csharp
// Items with a non-null HistoryItemId are still linked to history (not yet imported)
// Skip them to avoid health-checking files that are still being processed
query = query.Where(x => x.HistoryItemId == null);
```

This replaces the join-based approach with a simple column filter, which is more efficient and uses the new composite index.

Also verify the repair-time history check (lines ~649-682) — it should still work as-is since it checks `HistoryItem.DownloadDirId` which is a different concern (checking if arr has imported yet).

- [ ] **Step 2: Build and verify**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet build
```

- [ ] **Step 3: Commit**

```bash
git add backend/Services/HealthCheckService.cs
git commit -m "refactor: simplify health check filter using HistoryItemId column"
```

---

## Task 7: Final Verification and Startup Banner

**Files:**
- Modify: `backend/Program.cs:59` (startup banner)

- [ ] **Step 1: Update startup banner**

```csharp
Log.Warning("  NzbDav Backend Starting - BUILD v2026-03-12-HISTORY-CLEANUP");
Log.Warning("  FEATURE: History-aware cleanup with HistoryItemId tracking");
```

- [ ] **Step 2: Full build**

```bash
cd backend && /opt/homebrew/Cellar/dotnet/10.0.103/libexec/dotnet build
```

- [ ] **Step 3: Commit and push**

```bash
git add backend/Program.cs
git commit -m "chore: update startup banner for history-aware cleanup"
git push origin upstream/dotnet10-upgrade
```

---

## Testing Checklist

After CI builds and deploys the Docker image:

1. **Migration runs**: Check logs for successful migration adding `HistoryItemId` column and `HistoryCleanupItems` table
2. **New downloads get HistoryItemId**: Import an NZB, check that the mount folder and child DavItems have `HistoryItemId` set to the HistoryItem's ID
3. **HistoryCleanupService works**: Delete a history item from the UI with "delete files" — verify DavItems are cleaned up asynchronously
4. **Archive path works**: Let Sonarr/Radarr remove a history item — verify items are archived (not deleted) and eventually cleaned up
5. **RemoveUnlinkedFilesTask protects history items**: Run the "Remove Orphaned Files" maintenance task — verify items with `HistoryItemId` are NOT deleted
6. **Health checks skip history-linked items**: Verify files with `HistoryItemId` are excluded from the health check queue
7. **Existing data**: Existing DavItems will have `HistoryItemId = NULL` — this is expected and means they are eligible for orphan cleanup (which is the current behavior)
