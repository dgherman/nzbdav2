using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseClient(DavDatabaseContext ctx)
{
    public DavDatabaseContext Ctx => ctx;

    // file
    public Task<DavItem?> GetFileById(string id)
    {
        var guid = Guid.Parse(id);
        return ctx.Items.AsNoTracking().Where(i => i.Id == guid).FirstOrDefaultAsync();
    }

    public Task<List<DavItem>> GetFilesByIdPrefix(string prefix)
    {
        return ctx.Items.AsNoTracking()
            .Where(i => i.IdPrefix == prefix)
            .Where(i => i.Type == DavItem.ItemType.NzbFile
                        || i.Type == DavItem.ItemType.RarFile
                        || i.Type == DavItem.ItemType.MultipartFile)
            .ToListAsync();
    }

    // directory
    public Task<List<DavItem>> GetDirectoryChildrenAsync(Guid dirId, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking().Where(x => x.ParentId == dirId).ToListAsync(ct);
    }

    public Task<DavItem?> GetDirectoryChildAsync(Guid dirId, string childName, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking().FirstOrDefaultAsync(x => x.ParentId == dirId && x.Name == childName, ct);
    }

    public async Task<long> GetRecursiveSize(Guid dirId, CancellationToken ct = default)
    {
        if (dirId == DavItem.Root.Id)
        {
            return await Ctx.Items.SumAsync(x => x.FileSize, ct).ConfigureAwait(false) ?? 0;
        }

        const string sql = @"
            WITH RECURSIVE RecursiveChildren AS (
                SELECT Id, FileSize
                FROM DavItems
                WHERE ParentId = @parentId

                UNION ALL

                SELECT d.Id, d.FileSize
                FROM DavItems d
                INNER JOIN RecursiveChildren rc ON d.ParentId = rc.Id
            )
            SELECT IFNULL(SUM(FileSize), 0)
            FROM RecursiveChildren;
        ";
        var connection = Ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@parentId";
        parameter.Value = dirId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    // nzbfile
    public async Task<DavNzbFile?> GetNzbFileAsync(Guid id, CancellationToken ct = default)
    {
        return await ctx.NzbFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
    }

    // queue
    public async Task<(QueueItem? queueItem, QueueNzbContents? queueNzbContents)> GetTopQueueItem
    (
        CancellationToken ct = default
    )
    {
        var nowTime = DateTime.Now;
        Serilog.Log.Debug("[DavDatabaseClient] GetTopQueueItem called. CurrentTime: {Now}", nowTime);

        var queueItem = await Ctx.QueueItems.AsNoTracking()
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Where(q => q.PauseUntil == null || nowTime >= q.PauseUntil)
            .Skip(0)
            .Take(1)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (queueItem != null)
        {
            Serilog.Log.Information("[DavDatabaseClient] Selected queue item: {Id} ({JobName}), Priority: {Priority}, PauseUntil: {PauseUntil}, CreatedAt: {CreatedAt}",
                queueItem.Id, queueItem.JobName, queueItem.Priority, queueItem.PauseUntil, queueItem.CreatedAt);
        }
        else
        {
            Serilog.Log.Debug("[DavDatabaseClient] No queue items available");
        }

        var queueNzbContents = queueItem != null
            ? await Ctx.QueueNzbContents.AsNoTracking().FirstOrDefaultAsync(q => q.Id == queueItem.Id, ct).ConfigureAwait(false)
            : null;

        if (queueItem != null && queueNzbContents == null)
        {
            Serilog.Log.Warning("[DavDatabaseClient] Found queue item {Id} but no NZB contents!", queueItem.Id);
        }

        return (queueItem, queueNzbContents);
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        int start = 0,
        int limit = int.MaxValue,
        string? search = null,
        CancellationToken ct = default
    )
    {
        var queueItems = Ctx.QueueItems.AsNoTracking();

        if (category != null)
            queueItems = queueItems.Where(q => q.Category == category);

        if (!string.IsNullOrWhiteSpace(search))
            queueItems = queueItems.Where(q => q.JobName.Contains(search) || q.FileName.Contains(search));

        return queueItems
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Skip(start)
            .Take(limit)
            .ToArrayAsync(cancellationToken: ct);
    }

    public Task<int> GetQueueItemsCount(string? category, string? search = null, CancellationToken ct = default)
    {
        var queueItems = Ctx.QueueItems.AsQueryable();

        if (category != null)
            queueItems = queueItems.Where(q => q.Category == category);

        if (!string.IsNullOrWhiteSpace(search))
            queueItems = queueItems.Where(q => q.JobName.Contains(search) || q.FileName.Contains(search));

        return queueItems.CountAsync(cancellationToken: ct);
    }

    public Task<QueueItem?> GetQueueItemById(Guid id, CancellationToken ct = default)
    {
        return Ctx.QueueItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken: ct);
    }

    public async Task SaveChanges(CancellationToken ct = default)
    {
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveQueueItemsAsync(List<Guid> ids, CancellationToken ct = default)
    {
        await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // history
    public async Task<HistoryItem?> GetHistoryItemAsync(string id)
    {
        return await Ctx.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Guid.Parse(id)).ConfigureAwait(false);
    }



    public async Task ArchiveHistoryItemsAsync(List<Guid> ids, CancellationToken ct = default)
    {
        var historyItems = await Ctx.HistoryItems
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        if (historyItems.Count == 0) return;

        Serilog.Log.Information("[DavDatabaseClient] Archiving {Count} history items: {Names}", 
            historyItems.Count, string.Join(", ", historyItems.Select(h => h.JobName)));

        foreach (var item in historyItems)
        {
            if (item.IsArchived) continue;
            item.IsArchived = true;
            item.ArchivedAt = DateTime.UtcNow;
        }
    }

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

        if (deleteFiles)
        {
            var dirIds = historyItems
                .Where(h => h.DownloadDirId != null)
                .Select(h => h.DownloadDirId!)
                .ToList();

            if (dirIds.Count > 0)
            {
                Serilog.Log.Information("[DavDatabaseClient] Deleting associated files for {Count} history items", dirIds.Count);
                await Ctx.Items
                    .Where(d => dirIds.Contains(d.Id))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
        }

        // Delete the history items from the database
        Ctx.HistoryItems.RemoveRange(historyItems);
    }

    public async Task CleanupOldHiddenHistoryItemsAsync(int daysToKeep = 30, CancellationToken ct = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);

        // Delete files for old hidden items
        await Ctx.Items
            .Where(d => Ctx.HistoryItems
                .Where(h => h.IsHidden && h.HiddenAt != null && h.HiddenAt < cutoffDate && h.DownloadDirId != null)
                .Select(h => h.DownloadDirId!)
                .Contains(d.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Delete old hidden history items
        await Ctx.HistoryItems
            .Where(h => h.IsHidden && h.HiddenAt != null && h.HiddenAt < cutoffDate)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private class FileSizeResult
    {
        public long TotalSize { get; init; }
    }

    // health check
    public async Task<List<HealthCheckStat>> GetHealthCheckStatsAsync
    (
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default
    )
    {
        return await Ctx.HealthCheckStats.AsNoTracking()
            .Where(h => h.DateStartInclusive >= from && h.DateStartInclusive <= to)
            .GroupBy(h => new { h.Result, h.RepairStatus })
            .Select(g => new HealthCheckStat
            {
                Result = g.Key.Result,
                RepairStatus = g.Key.RepairStatus,
                Count = g.Select(r => r.Count).Sum(),
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // completed-symlinks
    public async Task<List<DavItem>> GetCompletedSymlinkCategoryChildren(string category,
        CancellationToken ct = default)
    {
        var query = from historyItem in Ctx.HistoryItems.AsNoTracking()
            where historyItem.Category == category
                  && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                  && historyItem.DownloadDirId != null
            join davItem in Ctx.Items.AsNoTracking() on historyItem.DownloadDirId equals davItem.Id
            where davItem.Type == DavItem.ItemType.Directory
            select davItem;
        return await query.Distinct().ToListAsync(ct).ConfigureAwait(false);
    }
}