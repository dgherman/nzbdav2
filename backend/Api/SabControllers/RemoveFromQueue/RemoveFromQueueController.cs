using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromQueue;

public class RemoveFromQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromQueueResponse> RemoveFromQueue(RemoveFromQueueRequest request)
    {
        try
        {
            await queueManager.RemoveQueueItemsAsync(request.NzoIds, dbClient, request.CancellationToken).ConfigureAwait(false);
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, string.Join(",", request.NzoIds));
        }
        catch (Exception ex)
        {
            // Always return success to Sonarr/Radarr to prevent errors on their side
            // If item doesn't exist or there's a temporary failure, returning success is harmless
            Serilog.Log.Warning("[RemoveFromQueue] Failed to remove items {Ids}, but returning success to prevent Arr errors: {Message}",
                string.Join(",", request.NzoIds), ex.Message);
        }

        return new RemoveFromQueueResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromQueueRequest.New(httpContext).ConfigureAwait(false);
        return Ok(await RemoveFromQueue(request).ConfigureAwait(false));
    }
}