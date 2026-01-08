using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using Serilog;
using System.Text;

namespace NzbWebDAV.Api.Controllers.Maintenance;

[ApiController]
[Route("api/maintenance/repair-classification/{davItemId}")]
public class RepairClassificationController(
    DavDatabaseClient dbClient,
    QueueManager queueManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var davItemId = RouteData.Values["davItemId"]?.ToString();
        if (string.IsNullOrEmpty(davItemId) || !Guid.TryParse(davItemId, out var itemGuid))
        {
            return BadRequest(new { error = "Invalid DavItemId" });
        }

        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == itemGuid)
            .ConfigureAwait(false);

        if (davItem == null)
        {
            return NotFound(new { error = "File not found" });
        }

        Log.Information("[RepairClassification] Starting repair for item: {FileName} ({Id})", davItem.Name, davItem.Id);

        // 1. Get original NZB content
        // Try to find job name (parent directory)
        var jobName = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
        if (string.IsNullOrEmpty(jobName))
        {
            return BadRequest(new { error = "Could not determine job name from path" });
        }

        string? nzbContents = null;
        string? category = "default";

        // Try HistoryItems first
        var historyItem = await dbClient.Ctx.HistoryItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.JobName == jobName)
            .ConfigureAwait(false);

        if (historyItem != null && !string.IsNullOrEmpty(historyItem.NzbContents))
        {
            nzbContents = historyItem.NzbContents;
            category = historyItem.Category;
        }
        else
        {
            // Try QueueNzbContents if it was somehow left behind or is still in queue
            var queueItem = await dbClient.Ctx.QueueItems
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.JobName == jobName)
                .ConfigureAwait(false);

            if (queueItem != null)
            {
                var queueNzb = await dbClient.Ctx.QueueNzbContents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == queueItem.Id)
                    .ConfigureAwait(false);
                
                if (queueNzb != null)
                {
                    nzbContents = queueNzb.NzbContents;
                    category = queueItem.Category;
                }
            }
        }

        // Fallback: Generate from Database Metadata if XML is missing
        if (string.IsNullOrEmpty(nzbContents))
        {
            Log.Information("[RepairClassification] NZB XML not found in history for {JobName}. Attempting to regenerate from database segments...", jobName);
            
            var nzbFile = await dbClient.Ctx.NzbFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemGuid).ConfigureAwait(false);
            var rarFile = nzbFile == null ? await dbClient.Ctx.RarFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemGuid).ConfigureAwait(false) : null;
            var multipartFile = (nzbFile == null && rarFile == null) ? await dbClient.Ctx.MultipartFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemGuid).ConfigureAwait(false) : null;

            if (nzbFile != null || rarFile != null || multipartFile != null)
            {
                nzbContents = GenerateNzbXml(davItem, nzbFile, rarFile, multipartFile);
                Log.Information("[RepairClassification] Successfully regenerated NZB XML for {JobName}", jobName);
            }
        }

        if (string.IsNullOrEmpty(nzbContents))
        {
            return BadRequest(new { error = "Could not find or regenerate NZB content for this item. Classification cannot be repaired." });
        }

        // 2. Delete the old item and all its files in the same job directory
        var parentFolder = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == davItem.ParentId)
            .ConfigureAwait(false);

        // Capture category from path if historyItem was null
        if (category == "default" && davItem.Path.Contains("/content/"))
        {
            var parts = davItem.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Expected: content, category, jobname, filename
            if (parts.Length >= 2 && parts[0] == "content")
            {
                category = parts[1];
            }
        }

        if (parentFolder != null && parentFolder.Name == jobName)
        {
            Log.Information("[RepairClassification] Deleting entire job folder: {JobName}", jobName);
            await RemoveDavItemsAsync([parentFolder.Id], true, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            Log.Information("[RepairClassification] Deleting single item: {FileName}", davItem.Name);
            await RemoveDavItemsAsync([davItem.Id], true, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        // 3. Re-add to queue
        var newQueueId = Guid.NewGuid();
        var newQueueItem = new QueueItem
        {
            Id = newQueueId,
            CreatedAt = DateTime.Now,
            FileName = historyItem?.FileName ?? davItem.Name,
            JobName = jobName,
            Category = category ?? "default",
            Priority = 0,
            TotalSegmentBytes = historyItem?.TotalSegmentBytes ?? davItem.FileSize ?? 0
        };

        var newNzbContent = new QueueNzbContents
        {
            Id = newQueueId,
            NzbContents = nzbContents
        };

        dbClient.Ctx.QueueItems.Add(newQueueItem);
        dbClient.Ctx.QueueNzbContents.Add(newNzbContent);

        // Also remove from history if it exists there
        if (historyItem != null)
        {
            dbClient.Ctx.HistoryItems.Remove(historyItem);
        }

        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        
        // 4. Awaken queue
        queueManager.AwakenQueue();

        Log.Information("[RepairClassification] Item {JobName} re-queued for deobfuscation repair.", jobName);

        return Ok(new { message = $"Item '{jobName}' has been removed from library and re-queued for deobfuscation repair." });
    }

    private async Task RemoveDavItemsAsync(List<Guid> ids, bool recursive, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        if (recursive)
        {
            // Find all children recursively
            var allIds = new List<Guid>(ids);
            var currentLevelIds = ids;

            while (currentLevelIds.Count > 0)
            {
                var nextLevelIds = await dbClient.Ctx.Items
                    .Where(x => x.ParentId != null && currentLevelIds.Contains(x.ParentId.Value))
                    .Select(x => x.Id)
                    .ToListAsync(ct);
                
                allIds.AddRange(nextLevelIds);
                currentLevelIds = nextLevelIds;
            }

            // Delete in reverse order (children first)
            var uniqueIds = allIds.Distinct().Reverse().ToList();
            await dbClient.Ctx.Items
                .Where(x => uniqueIds.Contains(x.Id))
                .ExecuteDeleteAsync(ct);
        }
        else
        {
            await dbClient.Ctx.Items
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(ct);
        }
    }

    private static string GenerateNzbXml(DavItem davItem, DavNzbFile? nzbFile, DavRarFile? rarFile, DavMultipartFile? multipartFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
        sb.AppendLine("<!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\">");
        sb.AppendLine("<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\">");
        
        var date = davItem.ReleaseDate?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        if (nzbFile != null)
        {
            var subject = System.Net.WebUtility.HtmlEncode(davItem.Name);
            sb.Append($" <file poster=\"NzbDav\" date=\"{date}\" subject=\"{subject}\">\n");
            sb.AppendLine("  <groups><group>alt.binaries.misc</group></groups>");
            sb.AppendLine("  <segments>");
            
            var sizes = nzbFile.GetSegmentSizes();
            for (var i = 0; i < nzbFile.SegmentIds.Length; i++)
            {
                var size = sizes != null && i < sizes.Length ? sizes[i] : 0;
                var id = System.Net.WebUtility.HtmlEncode(nzbFile.SegmentIds[i]);
                sb.Append($"   <segment bytes=\"{size}\" number=\"{i + 1}\">{id}</segment>\n");
            }
            
            sb.AppendLine("  </segments>");
            sb.AppendLine(" </file>");
        }
        else if (rarFile != null)
        {
            for (var i = 0; i < rarFile.RarParts.Length; i++)
            {
                var part = rarFile.RarParts[i];
                var partName = $"{davItem.Name}.part{(i + 1):D2}.rar";
                sb.Append($" <file poster=\"NzbDav\" date=\"{date}\" subject=\"{System.Net.WebUtility.HtmlEncode(partName)}\">\n");
                sb.AppendLine("  <groups><group>alt.binaries.misc</group></groups>");
                sb.AppendLine("  <segments>");
                for (var j = 0; j < part.SegmentIds.Length; j++)
                {
                    var id = System.Net.WebUtility.HtmlEncode(part.SegmentIds[j]);
                    sb.Append($"   <segment bytes=\"0\" number=\"{j + 1}\">{id}</segment>\n");
                }
                sb.AppendLine("  </segments>");
                sb.AppendLine(" </file>");
            }
        }
        else if (multipartFile != null)
        {
            for (var i = 0; i < multipartFile.Metadata.FileParts.Length; i++)
            {
                var part = multipartFile.Metadata.FileParts[i];
                var partName = $"{davItem.Name}.{(i + 1):D3}";
                sb.Append($" <file poster=\"NzbDav\" date=\"{date}\" subject=\"{System.Net.WebUtility.HtmlEncode(partName)}\">\n");
                sb.AppendLine("  <groups><group>alt.binaries.misc</group></groups>");
                sb.AppendLine("  <segments>");
                for (var j = 0; j < part.SegmentIds.Length; j++)
                {
                    var id = System.Net.WebUtility.HtmlEncode(part.SegmentIds[j]);
                    sb.Append($"   <segment bytes=\"0\" number=\"{j + 1}\">{id}</segment>\n");
                }
                sb.AppendLine("  </segments>");
                sb.AppendLine(" </file>");
            }
        }
        
        sb.AppendLine("</nzb>");
        return sb.ToString();
    }
}