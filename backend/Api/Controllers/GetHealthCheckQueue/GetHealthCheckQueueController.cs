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

        if (request.ShowFailed)
        {
            // Filter for corrupted items (IsCorrupted flag)
            query = (IOrderedQueryable<DavItem>)query.Where(x => x.IsCorrupted);
        }

        if (request.ShowUnhealthy)
        {
            // Filter for items that have had an unhealthy health check result
            var unhealthyItemIds = dbClient.Ctx.HealthCheckResults
                .Where(r => r.Result == HealthCheckResult.HealthResult.Unhealthy)
                .Select(r => r.DavItemId)
                .Distinct();
            query = (IOrderedQueryable<DavItem>)query.Where(x => unhealthyItemIds.Contains(x.Id));
        }

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

        // Get latest health check results for all items
        var itemIds = pagedItems.Select(x => x.Id).ToList();
        var latestResults = await dbClient.Ctx.HealthCheckResults
            .AsNoTracking()
            .Where(r => itemIds.Contains(r.DavItemId))
            .GroupBy(r => r.DavItemId)
            .Select(g => new
            {
                DavItemId = g.Key,
                LatestResult = g.OrderByDescending(r => r.CreatedAt).First()
            })
            .ToDictionaryAsync(x => x.DavItemId, x => x.LatestResult).ConfigureAwait(false);

        return new GetHealthCheckQueueResponse()
        {
            UncheckedCount = totalCount,
            PendingCount = pendingCount,
            Items = pagedItems.Select(x =>
            {
                latestResults.TryGetValue(x.Id, out var result);
                return new GetHealthCheckQueueResponse.HealthCheckQueueItem()
                {
                    Id = x.Id.ToString(),
                    Name = x.Name,
                    Path = x.Path,
                    JobName = Path.GetFileName(Path.GetDirectoryName(x.Path)),
                    ReleaseDate = x.ReleaseDate,
                    LastHealthCheck = x.LastHealthCheck,
                    NextHealthCheck = x.NextHealthCheck == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : x.NextHealthCheck,
                    OperationType = x.NextHealthCheck == DateTimeOffset.MinValue ? "HEAD" : "STAT", // Urgent checks use HEAD, routine use STAT
                    Progress = 0, // Active progress is tracked via WebSocket during health checks
                    LatestResult = result?.Result.ToString() ?? (x.IsCorrupted ? "Unhealthy" : null)
                };
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