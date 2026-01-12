using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetFileDetails;

[ApiController]
[Route("api/file-details/{davItemId}")]
public class GetFileDetailsController(
    DavDatabaseClient dbClient,
    NzbProviderAffinityService affinityService,
    ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var davItemId = RouteData.Values["davItemId"]?.ToString();
        if (string.IsNullOrEmpty(davItemId) || !Guid.TryParse(davItemId, out var itemGuid))
        {
            return BadRequest(new { error = "Invalid DavItemId" });
        }

        var davItem = await dbClient.Ctx.Items
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == itemGuid)
            .ConfigureAwait(false);

        if (davItem == null)
        {
            return NotFound(new { error = "File not found" });
        }

        // Get the associated NZB file if it exists
        var nzbFile = await dbClient.Ctx.NzbFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == itemGuid)
            .ConfigureAwait(false);

        // JobName in NzbProviderStats is the full file path (davItem.Path)
        // JobName in QueueItems/HistoryItems is the directory name
        // We need to check both because Queue downloads key by JobName, while Streaming keys by Path.
        var providerStatsJobName = davItem.Path;
        var queueJobName = Path.GetFileName(Path.GetDirectoryName(davItem.Path));

        Serilog.Log.Debug("[GetFileDetails] Fetching stats for Path='{Path}' and Job='{Job}'", providerStatsJobName, queueJobName);

        // Get provider stats from database (check both keys)
        var dbProviderStats = await dbClient.Ctx.NzbProviderStats
            .AsNoTracking()
            .Where(x => x.JobName == providerStatsJobName || (queueJobName != null && x.JobName == queueJobName))
            .ToListAsync()
            .ConfigureAwait(false);

        // Get live stats from affinity service (check both keys)
        var liveProviderStats = affinityService.GetJobStats(providerStatsJobName);
        if (queueJobName != null)
        {
            var jobStats = affinityService.GetJobStats(queueJobName);
            // Serilog.Log.Information("[GetFileDetails] Live Stats - Path: {PathCount}, Job: {JobCount}", liveProviderStats.Count, jobStats.Count);
            foreach (var kvp in jobStats)
            {
                if (!liveProviderStats.ContainsKey(kvp.Key))
                {
                    liveProviderStats[kvp.Key] = kvp.Value;
                }
                else
                {
                    // If we have stats for both, merge them (summing up counts/bytes)
                    // Note: This is a simplification. Ideally we'd want weighted averages for speeds.
                    // For now, let's trust the Path-specific stats (Streaming) over Job stats if both exist?
                    // Actually, usually only one is active/populated significantly.
                    // Let's just accumulate counts to show total activity.
                    var existing = liveProviderStats[kvp.Key];
                    var incoming = kvp.Value;
                    existing.SuccessfulSegments += incoming.SuccessfulSegments;
                    existing.FailedSegments += incoming.FailedSegments;
                    existing.TotalBytes += incoming.TotalBytes;
                    existing.TotalTimeMs += incoming.TotalTimeMs;
                    // Keep the most recent LastUsed
                    if (incoming.LastUsed > existing.LastUsed) existing.LastUsed = incoming.LastUsed;
                    // Speed: Use the higher one? Or average? Let's use the one from the most recent activity.
                    if (incoming.LastUsed > existing.LastUsed) existing.RecentAverageSpeedBps = incoming.RecentAverageSpeedBps;
                }
            }
        }

        // Merge database and live stats (live stats take precedence)
        var mergedStats = new Dictionary<int, NzbProviderStats>();
        
        // First merge DB stats (accumulating if multiple entries for same provider exist due to different JobNames)
        foreach (var stat in dbProviderStats) 
        {
            if (mergedStats.TryGetValue(stat.ProviderIndex, out var existing))
            {
                existing.SuccessfulSegments += stat.SuccessfulSegments;
                existing.FailedSegments += stat.FailedSegments;
                existing.TotalBytes += stat.TotalBytes;
                existing.TotalTimeMs += stat.TotalTimeMs;
                if (stat.LastUsed > existing.LastUsed) existing.LastUsed = stat.LastUsed;
            }
            else
            {
                mergedStats[stat.ProviderIndex] = stat;
            }
        }
        
        // Then overwrite/update with live stats
        foreach (var kvp in liveProviderStats) mergedStats[kvp.Key] = kvp.Value;

        Serilog.Log.Debug("[GetFileDetails] Merged Stats Summary: {Count} providers. TotalBytes: {TotalBytes}",
            mergedStats.Count, mergedStats.Values.Sum(s => s.TotalBytes));

        // Get provider configuration to map provider index to host
        var usenetConfig = configManager.GetUsenetProviderConfig();
        var providerHosts = usenetConfig.Providers
            .Select((p, index) => new { Index = index, Host = p.Host })
            .ToDictionary(x => x.Index, x => x.Host);

        // Get missing article count
        var missingArticleCount = await dbClient.Ctx.MissingArticleEvents
            .AsNoTracking()
            .Where(x => x.Filename == davItem.Name)
            .CountAsync()
            .ConfigureAwait(false);

        // Get latest health check result
        var latestHealthCheck = await dbClient.Ctx.HealthCheckResults
            .AsNoTracking()
            .Where(x => x.DavItemId == itemGuid)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        // Get mapped path from LocalLinks
        var mappedLink = await dbClient.Ctx.LocalLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.DavItemId == itemGuid)
            .ConfigureAwait(false);

        // Generate download URL with token
        // Normalize path to match GetWebdavItemRequest normalization (strip leading slash)
        var normalizedPath = davItem.Path.TrimStart('/');
        var apiKey = EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(apiKey, normalizedPath);
        var downloadUrl = $"/view{davItem.Path}?downloadKey={downloadKey}";

        // Check if NZB download is available
        string? nzbDownloadUrl = null;

        var queueItem = await dbClient.Ctx.QueueItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.JobName == queueJobName)
            .ConfigureAwait(false);

        if (queueItem != null)
        {
            // If in queue, check QueueNzbContents
            var nzbExists = await dbClient.Ctx.QueueNzbContents
                .AsNoTracking()
                .AnyAsync(x => x.Id == queueItem.Id)
                .ConfigureAwait(false);
            
            if (nzbExists) nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
        }
        else
        {
            // If not in queue, check HistoryItem.NzbContents
            var historyItem = await dbClient.Ctx.HistoryItems
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.JobName == queueJobName)
                .ConfigureAwait(false);

            if (historyItem != null && !string.IsNullOrEmpty(historyItem.NzbContents))
            {
                nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
            }
        }

        // Fallback: If NZB not in Queue/History, check if we have any file metadata (Nzb, Rar, or Multipart) to generate from
        if (nzbDownloadUrl == null)
        {
            if (nzbFile != null)
            {
                nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
            }
            else
            {
                // Check Rar/Multipart
                var isRar = await dbClient.Ctx.RarFiles.AsNoTracking().AnyAsync(x => x.Id == itemGuid).ConfigureAwait(false);
                if (isRar)
                {
                    nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
                }
                else
                {
                    var isMultipart = await dbClient.Ctx.MultipartFiles.AsNoTracking().AnyAsync(x => x.Id == itemGuid).ConfigureAwait(false);
                    if (isMultipart)
                    {
                        nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
                    }
                }
            }
        }

        var response = new GetFileDetailsResponse
        {
            DavItemId = davItem.Id.ToString(),
            Name = davItem.Name,
            Path = davItem.Path,
            WebdavPath = davItem.Path,
            IdsPath = DavItem.SymlinkFolder.Path + "/" + davItem.Id,
            MappedPath = mappedLink?.LinkPath,
            JobName = queueJobName,
            DownloadUrl = downloadUrl,
            NzbDownloadUrl = nzbDownloadUrl,
            FileSize = davItem.FileSize ?? 0,
            ItemType = davItem.Type,
            ItemTypeString = davItem.Type.ToString(),
            CreatedAt = davItem.ReleaseDate,
            LastHealthCheck = davItem.LastHealthCheck,
            NextHealthCheck = davItem.NextHealthCheck,
            MediaInfo = davItem.MediaInfo,
            IsCorrupted = davItem.IsCorrupted,
            CorruptionReason = davItem.CorruptionReason,
            MissingArticleCount = missingArticleCount,
            ProviderStats = mergedStats.Values.OrderBy(ps => ps.ProviderIndex).Select(ps => new GetFileDetailsResponse.ProviderStatistic
            {
                ProviderIndex = ps.ProviderIndex,
                ProviderHost = providerHosts.GetValueOrDefault(ps.ProviderIndex, $"Provider {ps.ProviderIndex}"),
                SuccessfulSegments = ps.SuccessfulSegments,
                FailedSegments = ps.FailedSegments,
                TotalBytes = ps.TotalBytes,
                TotalTimeMs = ps.TotalTimeMs,
                LastUsed = ps.LastUsed,
                AverageSpeedBps = ps.RecentAverageSpeedBps > 0 ? ps.RecentAverageSpeedBps : ps.AverageSpeedBps,
                SuccessRate = ps.SuccessRate
            }).ToList(),
            LatestHealthCheckResult = latestHealthCheck != null ? new GetFileDetailsResponse.HealthCheckInfo
            {
                Result = latestHealthCheck.Result,
                RepairStatus = latestHealthCheck.RepairStatus,
                Message = latestHealthCheck.Message,
                CreatedAt = latestHealthCheck.CreatedAt
            } : null
        };

        // Get segment statistics if available
        if (nzbFile != null)
        {
            response.TotalSegments = nzbFile.SegmentIds?.Length ?? 0;

            // Calculate min/max/avg from segment sizes if available
            var segmentSizes = nzbFile.GetSegmentSizes();
            if (segmentSizes != null && segmentSizes.Length > 0)
            {
                response.MinSegmentSize = segmentSizes.Min();
                response.MaxSegmentSize = segmentSizes.Max();
                response.AvgSegmentSize = (long)segmentSizes.Average();
            }
        }

        return Ok(response);
    }
}
