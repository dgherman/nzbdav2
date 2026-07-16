# Dav Explore Delete Button — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a delete button to the Dav Explore page so users can delete files and folders directly from the UI.

**Architecture:** New backend API endpoint (`DELETE /api/dav-items/{id}`) with recursive CTE for directory deletion + frontend delete button on each item row with confirmation dialog.

**Tech Stack:** .NET 10, EF Core, SQLite, React (React Router), CSS Modules

**dotnet path:** `/opt/homebrew/opt/dotnet/bin/dotnet` (the default `dotnet` in PATH is .NET 9 and will fail)

---

### Task 1: Create backend `DELETE /api/dav-items/{id}` endpoint

**Files:**
- Create: `backend/Api/Controllers/DavItems/DeleteDavItemController.cs`

- [ ] **Step 1: Create the controller file**

Create `backend/Api/Controllers/DavItems/DeleteDavItemController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Api.Controllers.DavItems;

[ApiController]
[Route("api/dav-items/{id}")]
public class DeleteDavItemController(DavDatabaseContext dbContext) : BaseApiController
{
    [HttpDelete]
    public override async Task<IActionResult> HandleApiRequest()
    {
        return await base.HandleApiRequest().ConfigureAwait(false);
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        if (!Guid.TryParse((string?)RouteData.Values["id"], out var id))
            return BadRequest(new BaseApiResponse { Status = false, Error = "Invalid ID format" });

        var item = await dbContext.Items
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (item == null)
            return NotFound(new BaseApiResponse { Status = false, Error = "Item not found" });

        if (item.IsProtected())
            return StatusCode(403, new BaseApiResponse { Status = false, Error = "Cannot delete protected system directory" });

        // Collect paths before deletion for VFS cache invalidation
        List<string> affectedPaths;

        if (item.Type == DavItem.ItemType.Directory)
        {
            // Collect all descendant paths via recursive CTE
            affectedPaths = await dbContext.Database
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
                    id.ToString().ToUpper())
                .ToListAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            // Delete directory and all descendants via recursive CTE
            var deleted = await dbContext.Database
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
                    [id.ToString().ToUpper()],
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);

            Log.Information("[DavExplore] Deleted directory {Name} and {Count} items (Id={Id})",
                item.Name, deleted, id);
        }
        else
        {
            affectedPaths = item.Path != null ? [item.Path] : [];
            dbContext.Items.Remove(item);
            await dbContext.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

            Log.Information("[DavExplore] Deleted file {Name} (Id={Id})", item.Name, id);
        }

        // Trigger VFS cache invalidation
        var dirsToForget = affectedPaths
            .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToArray();
        DavDatabaseContext.TriggerVfsForget(dirsToForget!);

        return Ok(new BaseApiResponse { Status = true });
    }
}
```

Note: The `HandleApiRequest` override with `[HttpDelete]` is needed because `BaseApiController` only registers `[HttpGet]` and `[HttpPost]`. Adding `[HttpDelete]` on the override makes this endpoint respond to DELETE requests while preserving the auth/error handling from the base class.

- [ ] **Step 2: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet build --no-restore
```
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add backend/Api/Controllers/DavItems/DeleteDavItemController.cs
git commit -m "feat: add DELETE /api/dav-items/{id} endpoint for explore page"
```

---

### Task 2: Add `davItemId` to directory listings

**Files:**
- Modify: `backend/Api/Controllers/ListWebdavDirectory/ListWebdavDirectoryController.cs`

Currently `davItemId` is only set for files (line 30: `if (child is not IStoreCollection)`). Directories need IDs too so the frontend can call the delete endpoint.

- [ ] **Step 1: Update the listing to include directory IDs**

In `backend/Api/Controllers/ListWebdavDirectory/ListWebdavDirectoryController.cs`, replace the block that sets `davItemId` (lines 29-54) with logic that handles both files and directories:

Replace:
```csharp
            string? davItemId = null;
            if (child is not IStoreCollection) // Only for files, not directories
            {
                // Check for different file types that have direct access to DavItem
                if (child is DatabaseStoreNzbFile nzbFile)
                {
                    davItemId = nzbFile.DavItem.Id.ToString();
                }
                else if (child is DatabaseStoreMultipartFile multipartFile)
                {
                    davItemId = multipartFile.DavItem.Id.ToString();
                }
                else if (child is DatabaseStoreRarFile rarFile)
                {
                    davItemId = rarFile.DavItem.Id.ToString();
                }

                if (davItemId != null)
                {
                    logger.LogInformation("Found davItemId {DavItemId} for file: {FileName}", davItemId, child.Name);
                }
                else
                {
                    logger.LogWarning("File {FileName} type {Type} does not have DavItem property", child.Name, child.GetType().Name);
                }
            }
```

With:
```csharp
            string? davItemId = null;
            if (child is DatabaseStoreCollection dirCollection)
            {
                davItemId = dirCollection.UniqueKey;
            }
            else if (child is DatabaseStoreNzbFile nzbFile)
            {
                davItemId = nzbFile.DavItem.Id.ToString();
            }
            else if (child is DatabaseStoreMultipartFile multipartFile)
            {
                davItemId = multipartFile.DavItem.Id.ToString();
            }
            else if (child is DatabaseStoreRarFile rarFile)
            {
                davItemId = rarFile.DavItem.Id.ToString();
            }
```

- [ ] **Step 2: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet build --no-restore
```
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add backend/Api/Controllers/ListWebdavDirectory/ListWebdavDirectoryController.cs
git commit -m "feat: include davItemId for directories in webdav listing"
```

---

### Task 3: Add delete button and handler to Dav Explore frontend

**Files:**
- Modify: `frontend/app/routes/explore/route.tsx`
- Modify: `frontend/app/routes/explore/route.module.css`

- [ ] **Step 1: Add the `onDelete` callback**

In `frontend/app/routes/explore/route.tsx`, inside the `Body` function, after the `onTestDownload` callback (after line 260), add:

```tsx
    const onDelete = useCallback(async (davItemId: string, name: string) => {
        const confirmed = await confirm({
            title: "Delete Item",
            message: `Delete "${name}"? This will permanently remove this item and all its contents.`,
            confirmText: "Delete",
            variant: "danger"
        });

        if (!confirmed) return;

        try {
            const response = await fetch(`/api/dav-items/${davItemId}`, { method: 'DELETE' });
            if (!response.ok) {
                const data = await response.json();
                throw new Error(data.error || "Failed to delete");
            }
            addToast(`Deleted "${name}"`, "success", "Deleted");
            // Refresh the directory listing
            window.location.reload();
        } catch (e) {
            addToast(`Failed to delete "${name}": ${e}`, "danger", "Error");
        }
    }, [addToast, confirm]);
```

- [ ] **Step 2: Add delete button to directory items**

In the same file, find the directory item rendering (lines 283-288). Replace:

```tsx
                    {items.filter(x => x.isDirectory).map((x, index) =>
                        <Link key={`${index}_dir_item`} to={getDirectoryPath(x.name)} className={getClassName(x)}>
                            <div className={styles["directory-icon"]} />
                            <div className={styles["item-name"]}>{x.name}</div>
                        </Link>
                    )}
```

With:

```tsx
                    {items.filter(x => x.isDirectory).map((x, index) =>
                        <Link key={`${index}_dir_item`} to={getDirectoryPath(x.name)} className={getClassName(x)}>
                            <div className={styles["directory-icon"]} />
                            <div className={styles["item-name"]}>{x.name}</div>
                            {x.davItemId && (
                                <button
                                    className={styles["delete-button"]}
                                    onClick={(e) => { e.preventDefault(); e.stopPropagation(); onDelete(x.davItemId!, x.name); }}
                                    title="Delete"
                                >
                                    <i className="bi bi-trash"></i>
                                </button>
                            )}
                        </Link>
                    )}
```

- [ ] **Step 3: Add delete button to file items**

Find the file item rendering (lines 289-309). Replace:

```tsx
                    {items.filter(x => !x.isDirectory).map((x, index) =>
                        <div
                            key={`${index}_file_item`}
                            onClick={() => {
                                console.log('File clicked:', x.name, 'davItemId:', x.davItemId);
                                if (x.davItemId) {
                                    onFileClick(x.davItemId);
                                } else {
                                    console.warn('No davItemId for file:', x.name);
                                }
                            }}
                            className={getClassName(x)}
                            style={{ cursor: 'pointer' }}
                        >
                            <div className={getIcon(x as ExploreFile)} />
                            <div className={styles["item-info"]}>
                                <div className={styles["item-name"]}>{x.name}</div>
                                <div className={styles["item-size"]}>{formatFileSize(x.size)}</div>
                            </div>
                        </div>
                    )}
```

With:

```tsx
                    {items.filter(x => !x.isDirectory).map((x, index) =>
                        <div
                            key={`${index}_file_item`}
                            onClick={() => {
                                if (x.davItemId) {
                                    onFileClick(x.davItemId);
                                }
                            }}
                            className={getClassName(x)}
                            style={{ cursor: 'pointer' }}
                        >
                            <div className={getIcon(x as ExploreFile)} />
                            <div className={styles["item-info"]}>
                                <div className={styles["item-name"]}>{x.name}</div>
                                <div className={styles["item-size"]}>{formatFileSize(x.size)}</div>
                            </div>
                            {x.davItemId && (
                                <button
                                    className={styles["delete-button"]}
                                    onClick={(e) => { e.stopPropagation(); onDelete(x.davItemId!, x.name); }}
                                    title="Delete"
                                >
                                    <i className="bi bi-trash"></i>
                                </button>
                            )}
                        </div>
                    )}
```

- [ ] **Step 4: Add CSS for the delete button**

In `frontend/app/routes/explore/route.module.css`, add at the end (before the `.hidden` rule):

```css
.delete-button {
    margin-left: auto;
    flex-shrink: 0;
    background: none;
    border: 1px solid transparent;
    border-radius: 6px;
    color: #586e75;
    cursor: pointer;
    padding: 6px 10px;
    font-size: 16px;
    opacity: 0;
    transition: opacity 0.15s, color 0.15s, border-color 0.15s;
}

.item:hover .delete-button {
    opacity: 1;
}

.delete-button:hover {
    color: #dc322f;
    border-color: #dc322f40;
    background-color: #dc322f10;
}
```

- [ ] **Step 5: Verify frontend builds**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/frontend && npm run build
```
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add frontend/app/routes/explore/route.tsx frontend/app/routes/explore/route.module.css
git commit -m "feat: add delete button to Dav Explore page"
```

---

### Task 4: Add delete button to FileDetailsModal

**Files:**
- Modify: `frontend/app/routes/health/components/file-details-modal/file-details-modal.tsx`
- Modify: `frontend/app/routes/explore/route.tsx` (pass onDelete prop)

- [ ] **Step 1: Add `onDelete` prop to FileDetailsModal**

In `frontend/app/routes/health/components/file-details-modal/file-details-modal.tsx`, update the props type (line 8-18). Add `onDelete` to the type:

```typescript
export type FileDetailsModalProps = {
    show: boolean;
    onHide: () => void;
    fileDetails: FileDetails | null;
    loading: boolean;
    onResetStats?: (jobName: string) => void;
    onRunHealthCheck?: (id: string) => void;
    onAnalyze?: (id: string) => void;
    onRepair?: (id: string) => void;
    onTestDownload?: (id: string) => Promise<any>;
    onDelete?: (id: string, name: string) => void;
}
```

Update the function signature (line 20) to include the new prop:

```typescript
export function FileDetailsModal({ show, onHide, fileDetails, loading, onResetStats, onRunHealthCheck, onAnalyze, onRepair, onTestDownload, onDelete }: FileDetailsModalProps) {
```

- [ ] **Step 2: Add the Delete button in the Health Status section**

In the same file, find the Health Status section header buttons (around line 311, after the `<div style={{ display: 'flex', gap: '0.5rem' }}>` that contains Run Health Check and Repair buttons). After the Repair button block (after line 331's closing `})`), add:

```tsx
                                    {onDelete && (
                                        <button
                                            className="btn btn-outline-danger btn-sm"
                                            onClick={() => { onDelete(fileDetails.davItemId, fileDetails.fileName); onHide(); }}
                                            title="Permanently delete this file from the DAV"
                                        >
                                            <i className="bi bi-trash me-1"></i>
                                            Delete
                                        </button>
                                    )}
```

- [ ] **Step 3: Pass `onDelete` from explore page to FileDetailsModal**

In `frontend/app/routes/explore/route.tsx`, update the `FileDetailsModal` usage (around line 348-358). Add the `onDelete` prop:

Replace:
```tsx
            <FileDetailsModal
                show={showDetailsModal}
                onHide={onHideDetailsModal}
                fileDetails={selectedFileDetails}
                loading={loadingFileDetails}
                onResetStats={onResetFileStats}
                onRunHealthCheck={onRunHealthCheck}
                onAnalyze={onAnalyze}
                onRepair={onRepair}
                onTestDownload={onTestDownload}
            />
```

With:
```tsx
            <FileDetailsModal
                show={showDetailsModal}
                onHide={onHideDetailsModal}
                fileDetails={selectedFileDetails}
                loading={loadingFileDetails}
                onResetStats={onResetFileStats}
                onRunHealthCheck={onRunHealthCheck}
                onAnalyze={onAnalyze}
                onRepair={onRepair}
                onTestDownload={onTestDownload}
                onDelete={onDelete}
            />
```

- [ ] **Step 4: Verify frontend builds**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/frontend && npm run build
```
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add frontend/app/routes/health/components/file-details-modal/file-details-modal.tsx frontend/app/routes/explore/route.tsx
git commit -m "feat: add delete button to file details modal"
```

---

### Task 5: Update changelog and build version

**Files:**
- Modify: `README.md` (changelog section)
- Modify: `backend/Program.cs` (build version string)

- [ ] **Step 1: Add changelog entry**

Add a new version entry at the top of the `## Changelog` section in `README.md`. The current version is v0.6.14 with 4 commits from Tasks 1-4, so the new version is v0.6.18:

```markdown
## v0.6.18 (2026-03-25)
- Added: Delete button in Dav Explore allows direct deletion of files and folders from the virtual filesystem. Directories are recursively deleted. Protected system directories cannot be deleted.
```

- [ ] **Step 2: Update build version in `Program.cs`**

In `backend/Program.cs`, find the line containing `BUILD v` and update it to:

```
BUILD v2026-03-25-DAV-EXPLORE-DELETE
```

- [ ] **Step 3: Verify backend compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2/backend && /opt/homebrew/opt/dotnet/bin/dotnet build --no-restore
```
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add README.md backend/Program.cs
git commit -m "docs: update changelog and build version for Dav Explore delete button"
```

---

### Task 6: Push and verify CI

- [ ] **Step 1: Push to origin**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && git push origin main
```

- [ ] **Step 2: Verify CI build passes**

Check the GitHub Actions workflow to confirm the build succeeds.

- [ ] **Step 3: Provide test suggestions**

After pushing, test:
1. Navigate to Dav Explore, hover over a file — delete button (trash icon) should appear on the right
2. Click delete on a file — confirm dialog appears, confirm → file disappears from listing
3. Click delete on a directory — confirm dialog appears, confirm → directory and all contents removed
4. Verify protected directories (content, nzb, ids, symlink) do NOT have a delete button (they won't have davItemId if they're not DatabaseStoreCollection instances — but if they do, server rejects with 403)
5. Open a file details modal → Delete button appears alongside Repair and Health Check
6. Click Delete from modal → file deleted, modal closes, listing refreshes
7. After deleting a mount folder, verify the parent category folder (e.g., `/content/tv/`) still exists
