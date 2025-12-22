using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

[ApiController]
[Route("api/get-health-check-queue")]
public class GetHealthCheckQueueController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetHealthCheckQueueResponse> GetHealthCheckQueue(GetHealthCheckQueueRequest request)
    {
        var query = HealthCheckService.GetHealthCheckQueueItems(dbClient);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = (IOrderedQueryable<DavItem>)query.Where(x => EF.Functions.Like(x.Name, $"%{request.Search}%"));
        }

        var now = DateTimeOffset.UtcNow;
        var pendingCount = await query.Where(x => x.NextHealthCheck == null || x.NextHealthCheck <= now).CountAsync().ConfigureAwait(false);

        int totalCount;
        List<DavItem> pagedItems;

        if (request.ShowAll)
        {
            totalCount = await query.CountAsync().ConfigureAwait(false);
            pagedItems = await query
                .Skip(request.Page * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync().ConfigureAwait(false);
        }
        else
        {
            // Fetch a larger batch to allow for in-memory filtering of non-video files
            // Capped at 2000 to prevent OOM, assuming user won't page past 2000 video items often.
            var rawItems = await query.Take(2000).ToListAsync().ConfigureAwait(false);

            var filteredItems = rawItems
                .Where(x => FilenameUtil.IsVideoFile(x.Name))
                .ToList();

            totalCount = filteredItems.Count;

            pagedItems = filteredItems
                .Skip(request.Page * request.PageSize)
                .Take(request.PageSize)
                .ToList();
        }

        return new GetHealthCheckQueueResponse()
        {
            UncheckedCount = totalCount,
            PendingCount = pendingCount,
            Items = pagedItems.Select(x => new GetHealthCheckQueueResponse.HealthCheckQueueItem()
            {
                Id = x.Id.ToString(),
                Name = x.Name,
                Path = x.Path,
                ReleaseDate = x.ReleaseDate,
                LastHealthCheck = x.LastHealthCheck,
                NextHealthCheck = x.NextHealthCheck == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : x.NextHealthCheck,
                OperationType = x.NextHealthCheck == DateTimeOffset.MinValue ? "HEAD" : "STAT", // Urgent checks use HEAD, routine use STAT
            }).ToList(),
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckQueueRequest(HttpContext);
        var response = await GetHealthCheckQueue(request).ConfigureAwait(false);
        return Ok(response);
    }
}