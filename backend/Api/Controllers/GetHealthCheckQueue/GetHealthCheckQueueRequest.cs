using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

public class GetHealthCheckQueueRequest
{
    public int Page { get; init; } = 0;
    public int PageSize { get; init; } = 20;
    public string? Search { get; init; }
    public bool ShowAll { get; init; } = false;
    public bool ShowFailed { get; init; } = false;
    public CancellationToken CancellationToken { get; init; }

    public GetHealthCheckQueueRequest(HttpContext context)
    {
        var pageParam = context.GetQueryParam("page");
        var pageSizeParam = context.GetQueryParam("pageSize");
        var searchParam = context.GetQueryParam("search");
        var showAllParam = context.GetQueryParam("showAll");
        var showFailedParam = context.GetQueryParam("showFailed");
        CancellationToken = context.RequestAborted;

        if (pageParam is not null)
        {
            var isValidPageParam = int.TryParse(pageParam, out int page);
            if (!isValidPageParam) throw new BadHttpRequestException("Invalid page parameter");
            Page = page;
        }

        if (pageSizeParam is not null)
        {
            var isValidPageSizeParam = int.TryParse(pageSizeParam, out int pageSize);
            if (!isValidPageSizeParam) throw new BadHttpRequestException("Invalid pageSize parameter");
            PageSize = pageSize;
        }

        if (!string.IsNullOrWhiteSpace(searchParam))
        {
            Search = searchParam;
        }

        if (showAllParam is not null)
        {
            bool.TryParse(showAllParam, out bool showAll);
            ShowAll = showAll;
        }

        if (showFailedParam is not null)
        {
            bool.TryParse(showFailedParam, out bool showFailed);
            ShowFailed = showFailed;
        }
    }
}