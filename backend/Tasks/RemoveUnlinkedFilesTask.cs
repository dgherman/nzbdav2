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
        // Safety buffer: only consider items created at least 1 day ago
        var startTime = DateTime.Now.AddDays(-1);
        var linkedIdCount = await WriteLinkedIdsToTable();
        try
        {
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

                // Trigger vfs/forget for all affected directories
                var dirsToForget = _allRemovedPaths
                    .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToArray();
                DavDatabaseContext.TriggerVfsForget(dirsToForget!);

                Report($"Done. Removed {_allRemovedPaths.Count} unlinked files.");
            }
        }
        finally
        {
            await DropTmpTable();
        }
    }

    private async Task DropTmpTable()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS TMP_LINKED_FILES");
        }
        catch { /* best effort */ }
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
