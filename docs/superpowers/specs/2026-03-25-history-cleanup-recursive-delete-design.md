# History Cleanup: Recursive Mount Folder Deletion

## Problem

When a user deletes history items with "delete mounted files" checked, DavItems may not be deleted if they were previously unlinked by Sonarr/Radarr's automatic archive cleanup.

The flow that causes orphaned files:

1. Sonarr/Radarr sends delete request to nzbdav2
2. nzbdav2 archives the history item (`IsArchived = true`)
3. After retention period, `ArrMonitoringService` calls `RemoveHistoryItemsAsync` with `deleteFiles: false`
4. `HistoryCleanupService` unlinks DavItems (`HistoryItemId` set to `null`) but keeps them
5. User later manually deletes from History with "delete files" checked
6. `HistoryCleanupService` searches for DavItems by `HistoryItemId` — finds none (already `null`)
7. DavItems remain visible in Dav Explore

## Solution

Store the history item's `DownloadDirId` (mount folder ID) in `HistoryCleanupItem`. When deleting mounted files, use a recursive CTE to delete the mount directory and all descendants regardless of their `HistoryItemId` value.

## Changes

### 1. Data Model: `HistoryCleanupItem`

Add `DownloadDirId` (`Guid?`, nullable) to `HistoryCleanupItem`. Nullable for backwards compatibility — existing cleanup items and failed downloads without a mount folder will have `null`.

**File:** `backend/Database/Models/HistoryCleanupItem.cs`

### 2. Database Migration

New EF Core migration adding the `DownloadDirId` column to the `HistoryCleanupItems` table.

### 3. `RemoveHistoryItemsAsync`

When creating `HistoryCleanupItem` records, populate `DownloadDirId` from `historyItem.DownloadDirId`.

**File:** `backend/Database/DavDatabaseClient.cs`

### 4. `HistoryCleanupService`

When `DeleteMountedFiles` is true:

1. **Existing behavior (kept):** Delete DavItems where `HistoryItemId == cleanupItem.Id`. Handles items still linked.
2. **New:** If `DownloadDirId` is set, execute a recursive CTE:

```sql
WITH RECURSIVE descendants AS (
    SELECT Id FROM DavItems WHERE Id = @mountDirId
    UNION ALL
    SELECT d.Id FROM DavItems d
    INNER JOIN descendants a ON d.ParentId = a.Id
)
DELETE FROM DavItems WHERE Id IN (SELECT Id FROM descendants)
```

3. Collect affected paths before both deletions for VFS cache invalidation. Union paths from `HistoryItemId`-based lookup and `DownloadDirId`-based descendant lookup.

**File:** `backend/Services/HistoryCleanupService.cs`

### 5. VFS Cache Invalidation

No changes to the invalidation mechanism. The existing `TriggerVfsForget` call on affected directory paths covers both deletion methods. Just need to union the paths from both sources before triggering.

## Scope Boundaries

- Category folders (e.g., `/content/tv/`) are left untouched.
- The `RemoveUnlinkedFilesTask` is not modified — it remains available as a separate maintenance tool.
- `ArrMonitoringService` continues to pass `deleteFiles: false` — its behavior is unchanged.
- No frontend changes needed — the existing "delete mounted files" checkbox already sends the correct parameter.

## Edge Cases

- **`DownloadDirId` is null:** Falls back to existing `HistoryItemId`-only behavior. Applies to old cleanup items and failed downloads with no mount folder.
- **Mount folder already deleted:** Recursive CTE returns 0 rows. No-op.
- **DavItems partially unlinked:** Both methods together catch everything — `HistoryItemId` match catches still-linked items, recursive CTE catches unlinked items under the mount folder.
