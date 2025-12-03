using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.Stats;

[ApiController]
[Route("api/stats")]
public class StatsController(
    UsenetStreamingClient streamingClient,
    DavDatabaseContext dbContext,
    BandwidthService bandwidthService
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
    public Task<IActionResult> GetDeletedFiles([FromQuery] int limit = 100)
    {
        return ExecuteSafely(async () =>
        {
            var deleted = await dbContext.HealthCheckResults
                .AsNoTracking()
                .Where(x => x.RepairStatus == HealthCheckResult.RepairAction.Deleted)
                .OrderByDescending(x => x.CreatedAt)
                .Take(limit)
                .ToListAsync();

            return Ok(deleted);
        });
    }
}
