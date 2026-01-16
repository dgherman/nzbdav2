using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetProviderStats;

[ApiController]
[Route("api/provider-stats")]
public class GetProviderStatsController(ProviderStatsService statsService) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        // Extract hours parameter from query string, default to 24
        var hoursParam = HttpContext.Request.Query["hours"].FirstOrDefault();
        var hours = 24;
        if (hoursParam != null && int.TryParse(hoursParam, out var parsedHours))
        {
            hours = parsedHours;
        }

        var stats = statsService.GetCachedStats(hours);

        if (stats == null)
        {
            return Task.FromResult<IActionResult>(Ok(new ProviderStatsResponse
            {
                Providers = new List<ProviderStats>(),
                TotalOperations = 0,
                CalculatedAt = DateTimeOffset.UtcNow,
                TimeWindow = TimeSpan.FromHours(hours),
                TimeWindowHours = hours
            }));
        }

        return Task.FromResult<IActionResult>(Ok(stats));
    }
}
