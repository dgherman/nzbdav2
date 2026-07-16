# Dav Explore: Delete Files and Folders

## Problem

Users cannot delete files or folders directly from the Dav Explore UI. When shows/movies are removed from Sonarr/Radarr, or when the history cleanup doesn't fully clean up virtual files, there's no way to manually remove them from the DAV tree without direct database access.

## Solution

Add a delete button to each item (file and directory) in Dav Explore. Clicking it shows a confirmation dialog, then calls a new backend API endpoint that deletes the item (and all descendants for directories) from the database.

## Changes

### 1. Backend: New API Endpoint `DELETE /api/dav-items/{id}`

**File:** Create `backend/Api/Controllers/DavItems/DeleteDavItemController.cs`

A new controller that:
- Looks up the DavItem by GUID
- Returns 404 if not found
- Returns 403 if the item is protected (Root, ContentFolder, NzbFolder, IdsFolder, SymlinkFolder)
- **Files:** Deletes the single DavItem row
- **Directories:** Uses a recursive CTE to delete all descendants and the directory itself:

```sql
WITH RECURSIVE descendants AS (
    SELECT Id, Path FROM DavItems WHERE Id = {0}
    UNION ALL
    SELECT d.Id, d.Path FROM DavItems d
    INNER JOIN descendants a ON d.ParentId = a.Id
)
DELETE FROM DavItems WHERE Id IN (SELECT Id FROM descendants)
```

- Collects affected paths before deletion and calls `DavDatabaseContext.TriggerVfsForget()` for cache invalidation
- Returns 200 on success

### 2. Backend: Add `davItemId` to Directory Listings

**File:** Modify `backend/Api/Controllers/ListWebdavDirectory/ListWebdavDirectoryController.cs`

Currently `davItemId` is only populated for files. Extend the listing to also include `davItemId` for directories, so the frontend can reference them for deletion. Directory collections (`DatabaseStoreCollection`) have access to their `DavItem` — extract the ID from them.

### 3. Frontend: Delete Button in Explore Page

**File:** Modify `frontend/app/routes/explore/route.tsx`

Add an `onDelete` callback:
- Takes `davItemId` and `name` parameters
- Shows confirmation dialog via `useConfirm()` with danger variant: "Delete {name}? This will permanently remove this item and all its contents."
- On confirm, calls `DELETE /api/dav-items/{id}`
- On success, calls `revalidator.revalidate()` to refresh the listing
- Shows toast on success ("Deleted {name}") and failure

Add a delete button (trash icon or similar) to each item row — both files and directories. The button should stop click propagation so it doesn't trigger navigation (directories) or file details modal (files).

**File:** Modify `frontend/app/routes/explore/route.module.css`

Add styles for the delete button — small, right-aligned within the item row, with hover state.

### 4. Frontend: Delete Button in FileDetailsModal

**File:** Modify `frontend/app/routes/health/components/file-details-modal/file-details-modal.tsx`

Add a "Delete" action button to the file details modal (alongside existing Repair, Analyze, Health Check buttons). This provides an alternative entry point for deletion when viewing file details. Pass `onDelete` callback from the explore page.

## Scope Boundaries

- Protected directories (Root, Content, NZB, IDs, Symlink folders) cannot be deleted — server rejects with 403
- Category folders (e.g., `/content/tv/`) are deletable — they are regular directories. The confirmation dialog makes this a deliberate action.
- No bulk delete — one item at a time. Keeps the UI simple and prevents accidental mass deletion.
- No undo — deletion is permanent. The confirmation dialog is the safety net.
- Search results do not get delete buttons — only the main directory listing. Search results show items from potentially different directories; keeping delete on the main listing avoids confusion.

## Edge Cases

- **Item already deleted:** API returns 404, frontend shows error toast.
- **Protected item:** API returns 403, frontend shows "Cannot delete this item" toast.
- **Directory with many children:** Recursive CTE handles any depth. Single SQL statement, no N+1.
- **Concurrent deletion:** If two users delete the same item, second gets 404. Harmless.
- **VFS invalidation:** Paths collected before deletion, `TriggerVfsForget` called after. Same pattern as HistoryCleanupService.
