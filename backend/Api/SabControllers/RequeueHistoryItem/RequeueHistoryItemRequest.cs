using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.RequeueHistoryItem;

public class RequeueHistoryItemRequest
{
    public Guid HistoryItemId { get; init; }
    public CancellationToken CancellationToken { get; set; }

    public RequeueHistoryItemRequest(HttpContext context, ConfigManager configManager)
    {
        var nzoId = context.GetQueryParam("nzo_id");
        if (nzoId == null || !Guid.TryParse(nzoId, out var parsedId))
        {
            throw new ArgumentException("Invalid or missing nzo_id parameter");
        }
        HistoryItemId = parsedId;
        CancellationToken = context.RequestAborted;
    }
}
