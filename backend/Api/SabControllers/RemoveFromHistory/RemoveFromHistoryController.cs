using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromHistory;

public class RemoveFromHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromHistoryResponse> RemoveFromHistory(RemoveFromHistoryRequest request)
    {
        var userAgent = httpContext.Request.Headers["User-Agent"].ToString();
        Serilog.Log.Information("[RemoveFromHistory] Received request to remove {Count} items. Ids: {Ids}. User-Agent: {UserAgent}", 
            request.NzoIds.Count, string.Join(",", request.NzoIds), userAgent);

        // Enforce retention policy: don't let external clients delete items newer than X hours
        var retentionHours = configManager.GetHistoryRetentionHours();
        var cutoffTime = DateTime.Now.AddHours(-retentionHours);

        // Get the items to check their completion time
        var itemsInHistory = await dbClient.Ctx.HistoryItems
            .Where(h => request.NzoIds.Contains(h.Id))
            .ToListAsync(request.CancellationToken).ConfigureAwait(false);

        var eligibleIds = itemsInHistory
            .Where(h => h.CompletedAt < cutoffTime)
            .Select(h => h.Id)
            .ToList();

        if (eligibleIds.Count < request.NzoIds.Count)
        {
            var ignoredCount = request.NzoIds.Count - eligibleIds.Count;
            Serilog.Log.Information("[RemoveFromHistory] Ignoring deletion of {IgnoredCount} items that are within the retention period ({RetentionHours}h). Cutoff: {CutoffTime}", 
                ignoredCount, retentionHours, cutoffTime);
        }

        if (eligibleIds.Count == 0)
        {
            return new RemoveFromHistoryResponse { Status = true };
        }

        await using var transaction = await dbClient.Ctx.Database.BeginTransactionAsync().ConfigureAwait(false);
        await dbClient.RemoveHistoryItemsAsync(eligibleIds, request.DeleteCompletedFiles, request.CancellationToken).ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(request.CancellationToken).ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", eligibleIds));
        return new RemoveFromHistoryResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromHistoryRequest.New(httpContext).ConfigureAwait(false);
        return Ok(await RemoveFromHistory(request).ConfigureAwait(false));
    }
}