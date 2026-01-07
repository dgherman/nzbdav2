using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetHistoryResponse> GetHistoryAsync(GetHistoryRequest request)
    {
        // get query
        IQueryable<HistoryItem> query = dbClient.Ctx.HistoryItems;

        // Filter archived items by default (unless show_archived or show_hidden is true)
        // We treat 'Hidden' and 'Archived' as effectively the same visibility toggle for the UI
        if (!request.ShowArchived && !request.ShowHidden)
            query = query.Where(q => !q.IsArchived);

        if (request.NzoIds.Count > 0)
            query = query.Where(q => request.NzoIds.Contains(q.Id));
        if (request.Category != null)
            query = query.Where(q => q.Category == request.Category);
        if (request.Status.HasValue)
            query = query.Where(q => q.DownloadStatus == request.Status.Value);
        // Always show hidden items - don't filter them out
        // if (!request.ShowHidden)
        //     query = query.Where(q => !q.IsHidden);
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(q => EF.Functions.Like(q.JobName, $"%{request.Search}%"));
        if (!string.IsNullOrWhiteSpace(request.FailureReason))
            query = query.Where(q => q.FailureReason == request.FailureReason);

        // get total count
        var totalCountPromise = query
            .CountAsync(request.CancellationToken);

        // get history items
        var historyItemsPromise = query
            .OrderByDescending(q => q.CreatedAt)
            .Skip(request.Start)
            .Take(request.Limit)
            .ToArrayAsync(request.CancellationToken);

        // await results
        var totalCount = await totalCountPromise.ConfigureAwait(false);
        var historyItems = await historyItemsPromise.ConfigureAwait(false);

        // get download folders
        var downloadFolderIds = historyItems.Select(x => x.DownloadDirId).ToHashSet();
        var davItems = await dbClient.Ctx.Items
            .Where(x => downloadFolderIds.Contains(x.Id))
            .ToArrayAsync(request.CancellationToken).ConfigureAwait(false);
        var davItemsDict = davItems
            .ToDictionary(x => x.Id, x => x);

        // get slots
        var slots = historyItems
            .Select(x =>
                GetHistoryResponse.HistorySlot.FromHistoryItem(
                    x,
                    x.DownloadDirId != null ? davItemsDict.GetValueOrDefault(x.DownloadDirId.Value) : null,
                    configManager
                )
            )
            .ToList();

        // return response
        return new GetHistoryResponse()
        {
            History = new GetHistoryResponse.HistoryObject()
            {
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetHistoryRequest(httpContext, configManager);
        return Ok(await GetHistoryAsync(request).ConfigureAwait(false));
    }
}