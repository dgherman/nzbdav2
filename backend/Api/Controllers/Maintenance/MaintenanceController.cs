using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        return await AnalyzeBulk(new AnalyzeRequest { DavItemIds = new List<Guid> { id } });
    }

    public class AnalyzeRequest
    {
        public List<Guid> DavItemIds { get; set; } = new();
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeBulk([FromBody] AnalyzeRequest request)
    {
        try
        {
            var apiKey = HttpContext.GetRequestApiKey();
            if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
                return Unauthorized(new { error = "API Key Incorrect" });

            if (request.DavItemIds == null || request.DavItemIds.Count == 0)
                return BadRequest(new { error = "DavItemIds is required" });

            var processedCount = 0;
            foreach (var id in request.DavItemIds)
            {
                // Fetch generic DavItem first
                var davItem = await dbClient.Ctx.Items
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == id)
                    .ConfigureAwait(false);

                if (davItem == null) continue;

                string[]? segmentIds = null;
                if (davItem.Type == DavItem.ItemType.NzbFile)
                {
                    var nzbFile = await dbClient.GetNzbFileAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
                    segmentIds = nzbFile?.SegmentIds;
                }

                nzbAnalysisService.TriggerAnalysisInBackground(id, segmentIds, force: true);
                processedCount++;
            }

            return Accepted(new { message = $"Analysis started in background for {processedCount} item(s)." });
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
