using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.ResetProviderStats;

[ApiController]
[Route("api/reset-provider-stats")]
public class ResetProviderStatsController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var jobName = Request.Query["jobName"].ToString();

        if (string.IsNullOrEmpty(jobName))
        {
            // Reset all provider stats
            await dbClient.Ctx.NzbProviderStats
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            return Ok(new { message = "All provider stats have been reset", deletedCount = -1 });
        }
        else
        {
            // Reset stats for specific job
            var deletedCount = await dbClient.Ctx.NzbProviderStats
                .Where(x => x.JobName == jobName)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            return Ok(new { message = $"Provider stats reset for job: {jobName}", deletedCount });
        }
    }
}
