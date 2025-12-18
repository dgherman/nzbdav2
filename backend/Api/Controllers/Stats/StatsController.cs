using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.Stats;

public class RepairRequest
{
    public List<string> FilePaths { get; set; } = new();
}

[ApiController]
[Route("api/stats")]
public class StatsController(
    UsenetStreamingClient streamingClient,
    DavDatabaseContext dbContext,
    BandwidthService bandwidthService,
    ProviderErrorService providerErrorService,
    HealthCheckService healthCheckService,
    ConfigManager configManager
) : ControllerBase
{
    private void EnsureAuthenticated()
    {
        var apiKey = HttpContext.GetRequestApiKey();
        if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
            throw new UnauthorizedAccessException("API Key Incorrect");
    }

    private async Task<IActionResult> ExecuteSafely(Func<Task<IActionResult>> action)
    {
        try
        {
            EnsureAuthenticated();
            return await action();
        }
        catch (UnauthorizedAccessException e)
        {
            return Unauthorized(new { error = e.Message });
        }
        catch (FileNotFoundException e)
        {
            return NotFound(new { error = e.Message });
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new { error = e.Message });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { error = e.Message });
        }
    }

    [HttpGet("connections")]
    public Task<IActionResult> GetActiveConnections()
    {
        return ExecuteSafely(() =>
        {
            var connections = streamingClient.GetActiveConnectionsByProvider();
            return Task.FromResult<IActionResult>(Ok(connections));
        });
    }

    [HttpGet("bandwidth/current")]
    public Task<IActionResult> GetCurrentBandwidth()
    {
        return ExecuteSafely(() =>
        {
            var stats = bandwidthService.GetBandwidthStats();
            return Task.FromResult<IActionResult>(Ok(stats));
        });
    }

    [HttpGet("bandwidth/history")]
    public Task<IActionResult> GetBandwidthHistory([FromQuery] string range = "1h")
    {
        return ExecuteSafely(async () =>
        {
            // range: 1h, 24h, 30d
            var now = DateTimeOffset.UtcNow;
            var cutoff = range switch
            {
                "1h" => now.AddHours(-1),
                "24h" => now.AddHours(-24),
                "30d" => now.AddDays(-30),
                _ => now.AddHours(-1)
            };

            var samples = await dbContext.BandwidthSamples
                .AsNoTracking()
                .Where(x => x.Timestamp >= cutoff)
                .OrderBy(x => x.Timestamp)
                .ToListAsync();

            if (range == "30d")
            {
                // Aggregate to hourly
                var aggregated = samples
                    .GroupBy(x => new { x.ProviderIndex, Hour = x.Timestamp.ToString("yyyy-MM-dd HH:00") })
                    .Select(g => new 
                    {
                        g.Key.ProviderIndex,
                        Timestamp = DateTimeOffset.Parse(g.Key.Hour),
                        Bytes = g.Sum(x => x.Bytes)
                    })
                    .OrderBy(x => x.Timestamp)
                    .ToList();
                return Ok(aggregated);
            }

            return Ok(samples);
        });
    }

    [HttpGet("deleted-files")]
    public Task<IActionResult> GetDeletedFiles([FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? search = null)
    {
        return ExecuteSafely(async () =>
        {
            var query = dbContext.HealthCheckResults
                .AsNoTracking()
                .Where(x => x.RepairStatus == HealthCheckResult.RepairAction.Deleted);

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(x => x.Path.Contains(search) || x.Message.Contains(search));
            }

            var totalCount = await query.CountAsync();

            var deleted = await query
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var items = deleted.Select(x =>
            {
                var parts = x.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // Expected path: /content/{Category}/{JobName}/{FileName}
                // parts[0] = content
                // parts[1] = Category
                // parts[2] = JobName
                var jobName = parts.Length >= 3 ? parts[2] : null;

                return new
                {
                    x.Id,
                    x.CreatedAt,
                    x.DavItemId,
                    x.Path,
                    x.Result,
                    x.RepairStatus,
                    x.Message,
                    JobName = jobName
                };
            });

            return Ok(new { items, totalCount });
        });
    }

    [HttpGet("missing-articles")]
    public Task<IActionResult> GetMissingArticles([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null, [FromQuery] bool? blocking = null, [FromQuery] bool? orphaned = null, [FromQuery] bool? isImported = null)
    {
        return ExecuteSafely(async () =>
        {
            var providerCount = configManager.GetUsenetProviderConfig().Providers.Count;
            var (items, totalCount) = await providerErrorService.GetFileSummariesPagedAsync(page, pageSize, providerCount, search, blocking, orphaned, isImported);
            return Ok(new { items, totalCount });
        });
    }

    [HttpDelete("missing-articles")]
    public Task<IActionResult> ClearMissingArticles([FromQuery] string? filename = null)
    {
        return ExecuteSafely(async () =>
        {
            if (!string.IsNullOrEmpty(filename))
            {
                await providerErrorService.ClearErrorsForFile(filename);
                return Ok(new { message = $"Missing articles for '{filename}' cleared successfully" });
            }

            await providerErrorService.ClearAllErrors();
            return Ok(new { message = "Missing articles log cleared successfully" });
        });
    }

    [HttpDelete("deleted-files")]
    public Task<IActionResult> ClearDeletedFiles()
    {
        return ExecuteSafely(async () =>
        {
            await dbContext.HealthCheckResults
                .Where(x => x.RepairStatus == HealthCheckResult.RepairAction.Deleted)
                .ExecuteDeleteAsync();

            return Ok(new { message = "Deleted files log cleared successfully" });
        });
    }

    [HttpPost("repair")]
    public Task<IActionResult> TriggerRepair([FromBody] RepairRequest request)
    {
        return ExecuteSafely(async () =>
        {
            if (request.FilePaths == null || request.FilePaths.Count == 0)
                return BadRequest(new { error = "FilePaths is required" });
                
            foreach (var filePath in request.FilePaths)
            {
                healthCheckService.TriggerManualRepairInBackground(filePath);
                await providerErrorService.ClearErrorsForFile(filePath);
            }
            return Ok(new { message = "Repair triggered successfully" });
        });
    }

    [HttpGet("mapped-files")]
    public Task<IActionResult> GetMappedFiles([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null)
    {
        return ExecuteSafely(async () =>
        {
            var (items, totalCount) = await OrganizedLinksUtil.GetMappedFilesPagedAsync(dbContext, configManager, page, pageSize, search);
            return Ok(new { items, totalCount });
        });
    }
}
