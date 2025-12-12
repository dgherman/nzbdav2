using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Api.Controllers;

namespace NzbWebDAV.Api.Controllers.RunHealthCheck;

[ApiController]
[Route("api/health/check/{id}")]
public class RunHealthCheckController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!Guid.TryParse((string?)RouteData.Values["id"], out var id))
        {
            return BadRequest("Invalid ID format");
        }

        var item = await dbClient.Ctx.Items.FirstOrDefaultAsync(x => x.Id == id).ConfigureAwait(false);
        if (item == null)
        {
            return NotFound("Item not found");
        }

        // Set NextHealthCheck to MinValue to prioritize it
        item.NextHealthCheck = DateTimeOffset.MinValue;
        await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new { Message = "Health check scheduled successfully" });
    }
}
