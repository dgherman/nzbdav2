using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetLogs;

public class GetLogsRequest
{
    public string? Level { get; init; }

    public GetLogsRequest(HttpContext context)
    {
        Level = context.GetQueryParam("level");
    }
}