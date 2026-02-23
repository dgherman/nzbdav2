using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.Controllers.RegisterLocalLink;

[ApiController]
[Route("api/locallinks")]
public class RegisterLocalLinkController(
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> RegisterLocalLink([FromBody] RegisterLocalLinkRequest request)
    {
        try
        {
            // Validate external API key
            var apiKey = HttpContext.GetRequestApiKey();
            if (apiKey == null || apiKey != configManager.GetApiKey())
                return Unauthorized(new { error = "API Key Required" });

            // Validate input
            if (string.IsNullOrWhiteSpace(request.NzoId) ||
                string.IsNullOrWhiteSpace(request.FileName) ||
                string.IsNullOrWhiteSpace(request.LinkPath))
                return BadRequest(new { error = "nzoId, fileName and linkPath are required" });

            if (!Guid.TryParse(request.NzoId, out var nzoId))
                return BadRequest(new { error = "nzoId must be a valid GUID" });

            // Resolve: HistoryItem → DownloadDirId → DavItem by filename
            var historyItem = await dbClient.Ctx.HistoryItems
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == nzoId, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (historyItem == null)
                return NotFound(new { error = $"History item not found: {request.NzoId}" });

            if (historyItem.DownloadDirId == null)
                return NotFound(new { error = "History item has no download directory" });

            var davItem = await dbClient.Ctx.Items
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.ParentId == historyItem.DownloadDirId &&
                    x.Name == request.FileName, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (davItem == null)
            {
                Log.Warning("[LocalLinks] DavItem not found for nzoId={NzoId} fileName={FileName}",
                    request.NzoId, request.FileName);
                return NotFound(new { error = $"File '{request.FileName}' not found under download directory" });
            }

            OrganizedLinksUtil.UpdateCacheEntry(davItem.Id, request.LinkPath);
            Log.Information("[LocalLinks] Registered: {FileName} → {LinkPath} (DavItemId={DavItemId})",
                request.FileName, request.LinkPath, davItem.Id);

            return Ok(new { davItemId = davItem.Id, message = "LocalLink registered successfully" });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { error = e.Message });
        }
    }
}
