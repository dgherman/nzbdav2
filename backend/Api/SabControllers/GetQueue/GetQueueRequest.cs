using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueRequest
{
    public int Start { get; init; } = 0;
    public int Limit { get; init; } = int.MaxValue;
    public string? Category { get; init; }
    public string? Search { get; init; }
    public CancellationToken CancellationToken { get; init; }


    public GetQueueRequest(HttpContext context)
    {
        var startParam = context.GetQueryParam("start");
        var limitParam = context.GetQueryParam("limit");
        Category = context.GetQueryParam("category");
        Search = context.GetQueryParam("search");
        CancellationToken = context.RequestAborted;

        if (startParam is not null)
        {
            var isValidStartParam = int.TryParse(startParam, out int start);
            if (!isValidStartParam) throw new BadHttpRequestException("Invalid start parameter");
            Start = start;
        }

        if (limitParam is not null)
        {
            var isValidLimit = int.TryParse(limitParam, out int limit);
            if (!isValidLimit) throw new BadHttpRequestException("Invalid limit parameter");
            // SABnzbd clients such as Sonarr/Radarr can request limit=0 to mean
            // "no limit". Treating it as Take(0) hides every queued item except
            // the synthetic active item pinned by GetQueueController.
            Limit = limit <= 0 ? int.MaxValue : limit;
        }
    }
}