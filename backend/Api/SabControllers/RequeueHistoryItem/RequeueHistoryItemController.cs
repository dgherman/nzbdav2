using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;
using Serilog;
using Usenet.Nzb;

namespace NzbWebDAV.Api.SabControllers.RequeueHistoryItem;

public class RequeueHistoryItemController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        try
        {
            var request = new RequeueHistoryItemRequest(httpContext, configManager);
            var response = await RequeueAsync(request).ConfigureAwait(false);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new RequeueHistoryItemResponse
            {
                Status = false,
                Error = ex.Message
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RequeueHistoryItem] Error requeuing history item: {Error}", ex.Message);
            return Ok(new RequeueHistoryItemResponse
            {
                Status = false,
                Error = ex.Message
            });
        }
    }

    private async Task<RequeueHistoryItemResponse> RequeueAsync(RequeueHistoryItemRequest request)
    {
        // Get the history item
        var historyItem = await dbClient.Ctx.HistoryItems
            .FirstOrDefaultAsync(x => x.Id == request.HistoryItemId, request.CancellationToken)
            .ConfigureAwait(false);

        if (historyItem == null)
        {
            return new RequeueHistoryItemResponse
            {
                Status = false,
                Error = "History item not found"
            };
        }

        if (string.IsNullOrEmpty(historyItem.NzbContents))
        {
            return new RequeueHistoryItemResponse
            {
                Status = false,
                Error = "NZB contents not available for this history item"
            };
        }

        // Load the NZB document to get metadata
        var documentBytes = Encoding.UTF8.GetBytes(historyItem.NzbContents);
        using var memoryStream = new MemoryStream(documentBytes);
        var document = await NzbDocument.LoadAsync(memoryStream).ConfigureAwait(false);

        // Generate unique filename to avoid UNIQUE constraint violations
        var baseFileName = historyItem.FileName;
        var fileName = baseFileName;
        var counter = 1;
        while (await dbClient.Ctx.QueueItems.AnyAsync(x => x.FileName == fileName, request.CancellationToken).ConfigureAwait(false))
        {
            var extension = Path.GetExtension(baseFileName);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(baseFileName);
            fileName = $"{nameWithoutExtension}.requeue{counter}{extension}";
            counter++;
        }

        // Create new queue item
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            FileName = fileName,
            JobName = historyItem.JobName,
            NzbFileSize = documentBytes.Length,
            TotalSegmentBytes = document.Files.SelectMany(x => x.Segments).Select(x => x.Size).Sum(),
            Category = historyItem.Category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.Default,
            PauseUntil = null
        };

        var queueNzbContents = new QueueNzbContents()
        {
            Id = queueItem.Id,
            NzbContents = historyItem.NzbContents,
        };

        dbClient.Ctx.QueueItems.Add(queueItem);
        dbClient.Ctx.QueueNzbContents.Add(queueNzbContents);

        // Remove the history item since it's been successfully requeued
        dbClient.Ctx.HistoryItems.Remove(historyItem);

        await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);

        var message = GetQueueResponse.QueueSlot.FromQueueItem(queueItem).ToJson();
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemAdded, message);
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, historyItem.Id.ToString());

        // Awaken the queue
        queueManager.AwakenQueue();

        Log.Information("[RequeueHistoryItem] Successfully requeued history item {HistoryItemId} as queue item {QueueItemId} and removed from history",
            request.HistoryItemId, queueItem.Id);

        return new RequeueHistoryItemResponse
        {
            Status = true,
            NzoId = queueItem.Id.ToString()
        };
    }
}
