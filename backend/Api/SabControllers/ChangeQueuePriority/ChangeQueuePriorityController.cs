using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.ChangeQueuePriority;

public class ChangeQueuePriorityController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var request = new ChangeQueuePriorityRequest(httpContext);

        // Get the queue item
        var queueItem = await dbClient.GetQueueItemById(request.NzoId, request.CancellationToken).ConfigureAwait(false);
        if (queueItem == null)
        {
            return BadRequest(new ChangeQueuePriorityResponse()
            {
                Status = false,
                Error = "Queue item not found"
            });
        }

        // Update priority based on action
        var newPriority = request.Action switch
        {
            "top" => QueueItem.PriorityOption.Force,
            "bottom" => QueueItem.PriorityOption.Low,
            "high" => QueueItem.PriorityOption.High,
            "normal" => QueueItem.PriorityOption.Normal,
            "low" => QueueItem.PriorityOption.Low,
            _ => queueItem.Priority
        };

        if (newPriority != queueItem.Priority)
        {
            queueItem.Priority = newPriority;

            // For "bottom" action, also update CreatedAt to ensure it goes to the absolute bottom
            if (request.Action == "bottom")
            {
                // Set CreatedAt to far future so it sorts to bottom within its priority
                queueItem.CreatedAt = DateTime.MaxValue.AddYears(-1);
            }
            // For "top" action, update CreatedAt to ensure it goes to the absolute top
            else if (request.Action == "top")
            {
                // Set CreatedAt to now so it sorts to top within Force priority
                queueItem.CreatedAt = DateTime.UtcNow;
            }

            await dbClient.SaveChanges(request.CancellationToken).ConfigureAwait(false);

            // Notify WebSocket clients of the priority change
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemPriorityChanged, request.NzoId.ToString());
        }

        return Ok(new ChangeQueuePriorityResponse()
        {
            Status = true,
            Priority = (int)newPriority
        });
    }
}
