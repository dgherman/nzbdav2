using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Utils;
using Serilog.Events;

namespace NzbWebDAV.Api.Controllers.GetLogs;

[ApiController]
[Route("api/get-logs")]
public class GetLogsController : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var request = new GetLogsRequest(HttpContext);
        
        var logs = InMemoryLogSink.Instance.GetLogs();
        
        if (!string.IsNullOrEmpty(request.Level) && Enum.TryParse<LogEventLevel>(request.Level, true, out var level))
        {
            logs = logs.Where(l => l.Level >= level);
        }
        
        var response = new GetLogsResponse
        {
            Logs = logs.OrderByDescending(x => x.Timestamp).Select(x => new GetLogsResponse.LogEntry
            {
                Timestamp = x.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                Level = x.Level.ToString(),
                Message = x.RenderMessage()
            }).ToList()
        };

        return Task.FromResult<IActionResult>(Ok(response));
    }
}