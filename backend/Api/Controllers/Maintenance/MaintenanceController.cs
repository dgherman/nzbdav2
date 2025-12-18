using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.Maintenance;

public class ResetConnectionsRequest
{
    public ConnectionUsageType? Type { get; set; }
}

[ApiController]
[Route("api/maintenance")]
public class MaintenanceController(UsenetStreamingClient usenetClient) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        return Task.FromResult<IActionResult>(NotFound());
    }

    [HttpPost("reset-connections")]
    public async Task<IActionResult> ResetConnections([FromBody] ResetConnectionsRequest request)
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
