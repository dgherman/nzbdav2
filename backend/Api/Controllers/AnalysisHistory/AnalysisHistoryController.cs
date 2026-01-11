using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.AnalysisHistory;

[ApiController]
[Route("api/analysis-history")]
public class AnalysisHistoryController(DavDatabaseContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<AnalysisHistoryItem>>> GetHistory([FromQuery] int page = 0, [FromQuery] int pageSize = 100, [FromQuery] string? search = null, [FromQuery] bool showFailedOnly = false)
    {
        var apiKey = HttpContext.GetRequestApiKey();
        if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
        {
            return Unauthorized(new { error = "Unauthorized" });
        }

        var query = db.AnalysisHistoryItems.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(x => x.FileName.ToLower().Contains(searchLower) || (x.JobName != null && x.JobName.ToLower().Contains(searchLower)));
        }

        if (showFailedOnly)
        {
            query = query.Where(x => x.Result == "Failed");
        }

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(items);
    }
}
