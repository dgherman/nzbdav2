using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Stats;

[ApiController]
[Route("api/stats")]
public class StatsController(
    UsenetStreamingClient streamingClient,
    DavDatabaseContext dbContext,
    BandwidthService bandwidthService
) : BaseApiController
{
    [HttpGet("connections")]
    public IActionResult GetActiveConnections()
    {
        var connections = streamingClient.GetActiveConnections();
        return Ok(connections);
    }

    [HttpGet("bandwidth/history")]
    public async Task<IActionResult> GetBandwidthHistory([FromQuery] string range = "1h")
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

        // Group/Aggregate based on range?
        // For now, just return raw samples. The frontend can aggregate if needed.
        // 1h = 60 samples per provider
        // 24h = 1440 samples per provider
        // 30d = 43200 samples per provider
        // 43k samples is a bit much to send JSON. Let's aggregate for 30d.

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

        if (range == "24h")
        {
             // Aggregate to 5-min? or just return raw 1-min is probably fine (1440 * providers)
             // Let's aggregate to 10 mins
             // Actually raw 1440 is fine.
             return Ok(samples);
        }

        return Ok(samples);
    }

    [HttpGet("deleted-files")]
    public async Task<IActionResult> GetDeletedFiles([FromQuery] int limit = 100)
    {
        var deleted = await dbContext.HealthCheckResults
            .AsNoTracking()
            .Where(x => x.RepairStatus == HealthCheckResult.RepairAction.Deleted)
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return Ok(deleted);
    }
}
