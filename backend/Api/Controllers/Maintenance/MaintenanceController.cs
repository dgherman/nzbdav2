using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Maintenance;

public class ResetConnectionsRequest
{
    public ConnectionUsageType? Type { get; set; }
}

[ApiController]
[Route("api/maintenance")]
public class MaintenanceController(
    UsenetStreamingClient usenetClient, 
    DavDatabaseClient dbClient,
    NzbAnalysisService nzbAnalysisService
) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        return Task.FromResult<IActionResult>(NotFound());
    }

    [HttpGet("active-analyses")]
    public IActionResult GetActiveAnalyses()
    {
        var apiKey = HttpContext.GetRequestApiKey();
        if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
            return Unauthorized(new { error = "API Key Incorrect" });

        return Ok(nzbAnalysisService.GetActiveAnalyses());
    }

    [HttpPost("analyze/{id}")]
    public async Task<IActionResult> Analyze(Guid id)
    {
        try
        {
            var apiKey = HttpContext.GetRequestApiKey();
            if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
                return Unauthorized(new { error = "API Key Incorrect" });

            var nzbFile = await dbClient.GetNzbFileAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
            if (nzbFile == null) return NotFound(new { error = "NZB file not found" });

            nzbAnalysisService.TriggerAnalysisInBackground(nzbFile.Id, nzbFile.SegmentIds, force: true);

            return Accepted(new { message = "Analysis started in background." });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { error = e.Message });
        }
    }

    [HttpPost("reset-connections")]    public async Task<IActionResult> ResetConnections([FromBody] ResetConnectionsRequest request)
    {
        try
        {
            var apiKey = HttpContext.GetRequestApiKey();
            if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
                return Unauthorized(new { error = "API Key Incorrect" });

            await usenetClient.ResetConnections(request.Type);
            return Ok(new { message = "Connections reset successfully." });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { error = e.Message });
        }
    }
}
