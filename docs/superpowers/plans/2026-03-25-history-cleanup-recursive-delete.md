# History Cleanup: Recursive Mount Folder Deletion — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix "delete mounted files" to actually remove DavItems even when they've been previously unlinked by Sonarr/Radarr archive cleanup.

**Architecture:** Add `DownloadDirId` to `HistoryCleanupItem` so the cleanup service can find and recursively delete the mount folder tree by directory structure, not just by `HistoryItemId` match.

**Tech Stack:** .NET 10, EF Core, SQLite, recursive CTE

**dotnet path:** `/opt/homebrew/opt/dotnet/bin/dotnet` (the default `dotnet` in PATH is .NET 9 and will fail)

---

### Task 1: Add `DownloadDirId` to `HistoryCleanupItem` model

**Files:**
- Modify: `backend/Database/Models/HistoryCleanupItem.cs`

- [ ] **Step 1: Add the property**

In `backend/Database/Models/HistoryCleanupItem.cs`, add the nullable `DownloadDirId` property:

```csharp
namespace NzbWebDAV.Database.Models;

public class HistoryCleanupItem
{
    public Guid Id { get; set; }
    public bool DeleteMountedFiles { get; set; }
    public Guid? DownloadDirId { get; set; }
}
```

- [ ] **Step 2: Register the property in `DavDatabaseContext`**

In `backend/Database/DavDatabaseContext.cs`, find the `HistoryCleanupItem` entity configuration (around line 481) and add the property mapping after the `DeleteMountedFiles` line:

```csharp
// HistoryCleanupItem
b.Entity<HistoryCleanupItem>(e =>
{
    e.ToTable("HistoryCleanupItems");
    e.HasKey(i => i.Id);
    e.Property(i => i.Id).ValueGeneratedNever();
    e.Property(i => i.DeleteMountedFiles).IsRequired();
    e.Property(i => i.DownloadDirId).IsRequired(false);
});
```

- [ ] **Step 3: Verify it compiles**

Run:
```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet build --no-restore
```
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add backend/Database/Models/HistoryCleanupItem.cs backend/Database/DavDatabaseContext.cs
git commit -m "feat: add DownloadDirId to HistoryCleanupItem model"
```

---

### Task 2: Create EF Core migration

**Files:**
- Create: `backend/Database/Migrations/<timestamp>_AddDownloadDirIdToHistoryCleanup.cs`
- Modify: `backend/Database/Migrations/DavDatabaseContextModelSnapshot.cs` (auto-generated)

- [ ] **Step 1: Generate the migration**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet ef migrations add AddDownloadDirIdToHistoryCleanup --project . --context DavDatabaseContext
```

Expected: A new migration file is created in `backend/Database/Migrations/`.

- [ ] **Step 2: Verify the generated migration**

Open the generated migration file and verify it contains:

```csharp
migrationBuilder.AddColumn<Guid>(
    name: "DownloadDirId",
    table: "HistoryCleanupItems",
    type: "TEXT",
    nullable: true);
```

And the `Down` method contains:

```csharp
migrationBuilder.DropColumn(
    name: "DownloadDirId",
    table: "HistoryCleanupItems");
```

If the migration contains anything else unexpected (e.g., changes to other tables), investigate before proceeding.

- [ ] **Step 3: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet build --no-restore
```
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add backend/Database/Migrations/
git commit -m "feat: add migration for DownloadDirId column on HistoryCleanupItems"
```

---

### Task 3: Populate `DownloadDirId` in `RemoveHistoryItemsAsync`

**Files:**
- Modify: `backend/Database/DavDatabaseClient.cs:214-219`

- [ ] **Step 1: Update the cleanup item creation**

In `backend/Database/DavDatabaseClient.cs`, find the `RemoveHistoryItemsAsync` method (line 199). Replace the `HistoryCleanupItems.AddRange` block (lines 214-219) with:

```csharp
        // Queue cleanup items for HistoryCleanupService to process asynchronously
        Ctx.HistoryCleanupItems.AddRange(historyItems.Select(x => new Models.HistoryCleanupItem
        {
            Id = x.Id,
            DeleteMountedFiles = deleteFiles,
            DownloadDirId = x.DownloadDirId
        }));
```

- [ ] **Step 2: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet build --no-restore
```
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add backend/Database/DavDatabaseClient.cs
git commit -m "feat: populate DownloadDirId when creating HistoryCleanupItems"
```

---

### Task 4: Update `HistoryCleanupService` with recursive directory deletion

**Files:**
- Modify: `backend/Services/HistoryCleanupService.cs`

- [ ] **Step 1: Rewrite the `ExecuteAsync` method**

Replace the entire content of `backend/Services/HistoryCleanupService.cs` with:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
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

                // Collect paths before bulk operations for VFS cache invalidation.
                // Union paths from HistoryItemId-based lookup and DownloadDirId-based descendant lookup.
                var affectedPaths = await dbContext.Items
                    .Where(x => x.HistoryItemId == cleanupItem.Id)
                    .Select(x => x.Path)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (cleanupItem.DownloadDirId.HasValue)
                {
                    var descendantPaths = await dbContext.Database
                        .SqlQueryRaw<string>(
                            """
                            WITH RECURSIVE descendants AS (
                                SELECT Id, Path FROM DavItems WHERE Id = {0}
                                UNION ALL
                                SELECT d.Id, d.Path FROM DavItems d
                                INNER JOIN descendants a ON d.ParentId = a.Id
                            )
                            SELECT Path AS Value FROM descendants WHERE Path IS NOT NULL
                            """,
                            cleanupItem.DownloadDirId.Value.ToString().ToUpper())
                        .ToListAsync(stoppingToken)
                        .ConfigureAwait(false);

                    affectedPaths = affectedPaths.Union(descendantPaths).ToList();
                }

                if (cleanupItem.DeleteMountedFiles)
                {
                    // 1. Delete DavItems still linked by HistoryItemId (existing behavior)
                    var deletedByHistoryId = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteDeleteAsync(stoppingToken)
                        .ConfigureAwait(false);

                    Log.Information("[HistoryCleanup] Deleted {Count} DavItems by HistoryItemId for {Id}",
                        deletedByHistoryId, cleanupItem.Id);

                    // 2. Delete mount directory and all descendants via recursive CTE
                    //    This catches DavItems that were previously unlinked (HistoryItemId set to null)
                    if (cleanupItem.DownloadDirId.HasValue)
                    {
                        var deletedByTree = await dbContext.Database
                            .ExecuteSqlRawAsync(
                                """
                                WITH RECURSIVE descendants AS (
                                    SELECT Id FROM DavItems WHERE Id = {0}
                                    UNION ALL
                                    SELECT d.Id FROM DavItems d
                                    INNER JOIN descendants a ON d.ParentId = a.Id
                                )
                                DELETE FROM DavItems WHERE Id IN (SELECT Id FROM descendants)
                                """,
                                [cleanupItem.DownloadDirId.Value.ToString().ToUpper()],
                                stoppingToken)
                            .ConfigureAwait(false);

                        Log.Information("[HistoryCleanup] Deleted {Count} DavItems by mount folder tree for {Id} (DownloadDirId={DirId})",
                            deletedByTree, cleanupItem.Id, cleanupItem.DownloadDirId.Value);
                    }
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

                // Trigger vfs/forget for affected directories
                var dirsToForget = affectedPaths
                    .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToArray();
                DavDatabaseContext.TriggerVfsForget(dirsToForget!);

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

- [ ] **Step 2: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet build --no-restore
```
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add backend/Services/HistoryCleanupService.cs
git commit -m "feat: use recursive CTE to delete mount folder tree in HistoryCleanupService"
```

---

### Task 5: Update changelog and build version

**Files:**
- Modify: `README.md` (changelog section)
- Modify: `backend/Program.cs` (build version string)

- [ ] **Step 1: Check current version**

Look at the CI workflow or the most recent changelog entry in `README.md` to determine the current version number. Increment the PATCH by the number of commits added in this feature (4 commits from Tasks 1-4).

- [ ] **Step 2: Add changelog entry**

Add a new version entry at the top of the `## Changelog` section in `README.md`:

```markdown
## vX.Y.Z (2026-03-25)
- Fixed: "Delete mounted files" now correctly removes virtual files even when they were previously unlinked by Sonarr/Radarr automatic archive cleanup. Uses recursive directory tree deletion via DownloadDirId instead of relying solely on HistoryItemId matching.
```

(Replace `vX.Y.Z` with the correct incremented version.)

- [ ] **Step 3: Update build version in `Program.cs`**

In `backend/Program.cs`, find the line containing `BUILD v` and update it to:

```
BUILD v2026-03-25-RECURSIVE-HISTORY-CLEANUP
```

- [ ] **Step 4: Commit**

```bash
git add README.md backend/Program.cs
git commit -m "docs: update changelog and build version for recursive history cleanup"
```

---

### Task 6: Push and verify CI

- [ ] **Step 1: Push to fork**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && git push myfork
```

- [ ] **Step 2: Verify CI build passes**

Check the GitHub Actions workflow at the nzbdav2 repository to confirm the build succeeds.

- [ ] **Step 3: Provide test suggestions**

After pushing, provide the user with specific test suggestions:
1. Add an NZB to the queue, let it complete and appear in History
2. Wait for Sonarr to import and send a delete request (or manually archive the history item)
3. After the archive cleanup unlinks the DavItems, manually delete from History with "delete mounted files" checked
4. Verify in Dav Explore that the mount folder and all its files are gone
5. Verify the category folder (e.g., `/content/tv/`) still exists
