using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ResetProviderStats;

[ApiController]
[Route("api/reset-provider-stats")]
public class ResetProviderStatsController(
    DavDatabaseClient dbClient,
    NzbProviderAffinityService affinityService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var jobName = Request.Query["jobName"].ToString();

        if (string.IsNullOrEmpty(jobName))
        {
            // Reset all provider stats in database
            await dbClient.Ctx.NzbProviderStats
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            // Also clear the in-memory cache
            affinityService.ClearAllStats();

            return Ok(new { message = "All provider stats have been reset (database + cache)", deletedCount = -1 });
        }
        else
        {
            // Reset stats for specific job in database
            var deletedCount = await dbClient.Ctx.NzbProviderStats
                .Where(x => x.JobName == jobName)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            // Also clear the in-memory cache for this job
            affinityService.ClearJobStats(jobName);

            return Ok(new { message = $"Provider stats reset for job: {jobName} (database + cache)", deletedCount });
        }
    }
}
