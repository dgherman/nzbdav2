using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.ChangeQueuePriority;

public class ChangeQueuePriorityRequest
{
    public Guid NzoId { get; set; }
    public string Action { get; set; } = null!;
    public CancellationToken CancellationToken { get; set; }

    public ChangeQueuePriorityRequest(HttpContext context)
    {
        var nzoIdStr = context.GetQueryParam("value") ?? context.GetQueryParam("nzo_id");
        if (string.IsNullOrEmpty(nzoIdStr) || !Guid.TryParse(nzoIdStr, out var nzoId))
            throw new BadHttpRequestException("Missing or invalid nzo_id/value parameter");

        var action = context.GetQueryParam("value2") ?? context.GetQueryParam("action");
        if (string.IsNullOrEmpty(action))
            throw new BadHttpRequestException("Missing action parameter (value2 or action)");

        // Validate action
        if (action != "top" && action != "bottom" && action != "high" && action != "normal" && action != "low")
            throw new BadHttpRequestException("Invalid action. Must be: top, bottom, high, normal, or low");

        NzoId = nzoId;
        Action = action.ToLowerInvariant();
        CancellationToken = context.RequestAborted;
    }
}
