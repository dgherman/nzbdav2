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

        // Detect if request is from external API client (Sonarr/Radarr) vs UI
        var isExternalClient = userAgent.Contains("Sonarr", StringComparison.OrdinalIgnoreCase) ||
                               userAgent.Contains("Radarr", StringComparison.OrdinalIgnoreCase);

        if (isExternalClient)
        {
            // External clients (Sonarr/Radarr): Archive items instead of deleting
            // This keeps Arr in sync while preserving items for 24h retention period
            Serilog.Log.Information("[RemoveFromHistory] External client request: Archiving {Count} items to maintain sync with Arr",
                request.NzoIds.Count);

            try
            {
                var itemsToArchive = await dbClient.Ctx.HistoryItems
                    .Where(h => request.NzoIds.Contains(h.Id))
                    .Where(h => !h.IsArchived) // Don't re-archive already archived items
                    .ToListAsync(request.CancellationToken).ConfigureAwait(false);

                foreach (var item in itemsToArchive)
                {
                    item.IsArchived = true;
                    item.ArchivedAt = DateTime.Now;
                }

                await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);

                Serilog.Log.Information("[RemoveFromHistory] Archived {Count} items. Will be deleted after 24h retention period.",
                    itemsToArchive.Count);

                // Notify UI to hide archived items
                _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", request.NzoIds));
            }
            catch (Exception ex)
            {
                // Always return success to prevent Arr errors
                Serilog.Log.Warning("[RemoveFromHistory] Failed to archive items {Ids}, but returning success to prevent Arr errors: {Message}",
                    string.Join(",", request.NzoIds), ex.Message);
            }
        }
        else
        {
            // UI requests: Allow immediate permanent deletion
            Serilog.Log.Information("[RemoveFromHistory] UI request: Permanently deleting {Count} items", request.NzoIds.Count);

            try
            {
                await using var transaction = await dbClient.Ctx.Database.BeginTransactionAsync().ConfigureAwait(false);
                await dbClient.RemoveHistoryItemsAsync(request.NzoIds, request.DeleteCompletedFiles, request.CancellationToken).ConfigureAwait(false);
                await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(request.CancellationToken).ConfigureAwait(false);
                _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", request.NzoIds));
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning("[RemoveFromHistory] Failed to delete items {Ids}: {Message}",
                    string.Join(",", request.NzoIds), ex.Message);
                throw; // UI should see errors
            }
        }

        return new RemoveFromHistoryResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromHistoryRequest.New(httpContext).ConfigureAwait(false);
        return Ok(await RemoveFromHistory(request).ConfigureAwait(false));
    }
}