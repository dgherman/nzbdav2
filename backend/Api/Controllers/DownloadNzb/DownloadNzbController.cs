using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using System.Text;

namespace NzbWebDAV.Api.Controllers.DownloadNzb;

[ApiController]
[Route("api/download-nzb/{davItemId}")]
public class DownloadNzbController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var davItemId = RouteData.Values["davItemId"]?.ToString();
        if (string.IsNullOrEmpty(davItemId) || !Guid.TryParse(davItemId, out var itemGuid))
        {
            return BadRequest(new { error = "Invalid DavItemId" });
        }

        // Get the DavItem
        var davItem = await dbClient.Ctx.Items
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == itemGuid)
            .ConfigureAwait(false);

        if (davItem == null)
        {
            return NotFound(new { error = "File not found" });
        }

        // Find the QueueItem or HistoryItem that matches this DavItem
        // We'll check both because the item might be in queue or history
        var jobName = Path.GetFileName(Path.GetDirectoryName(davItem.Path));

        // Try to find the queue item first
        var queueItem = await dbClient.Ctx.QueueItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.JobName == jobName)
            .ConfigureAwait(false);

        // If not in queue, try history
        string? nzbContentString = null;
        Guid? nzbContentsId = queueItem?.Id;
        
        if (queueItem != null)
        {
             // It is in the queue, so contents should be in QueueNzbContents
             var queueContents = await dbClient.Ctx.QueueNzbContents
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == queueItem.Id)
                .ConfigureAwait(false);
             nzbContentString = queueContents?.NzbContents;
        }
        else
        {
            // Not in queue, check history
            var historyItem = await dbClient.Ctx.HistoryItems
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.JobName == jobName)
                .ConfigureAwait(false);

            if (historyItem != null)
            {
                // History item stores contents directly
                nzbContentString = historyItem.NzbContents;
            }
        }

        if (string.IsNullOrEmpty(nzbContentString))
        {
            // Fallback: Generate from Database Metadata
            var nzbFile = await dbClient.Ctx.NzbFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == itemGuid)
                .ConfigureAwait(false);

            var rarFile = nzbFile == null ? await dbClient.Ctx.RarFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == itemGuid)
                .ConfigureAwait(false) : null;

            var multipartFile = (nzbFile == null && rarFile == null) ? await dbClient.Ctx.MultipartFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == itemGuid)
                .ConfigureAwait(false) : null;

            if (nzbFile != null || rarFile != null || multipartFile != null)
            {
                nzbContentString = GenerateNzbXml(davItem, nzbFile, rarFile, multipartFile);
            }
        }

        if (string.IsNullOrEmpty(nzbContentString))
        {
            return NotFound(new { error = "NZB contents not found" });
        }

        // Return the NZB file as a download
        var fileName = $"{jobName}.nzb";
        var contentBytes = Encoding.UTF8.GetBytes(nzbContentString);

        return File(
            contentBytes,
            "application/x-nzb",
            fileName
        );
    }

    private static string GenerateNzbXml(Database.Models.DavItem davItem, Database.Models.DavNzbFile? nzbFile, Database.Models.DavRarFile? rarFile, Database.Models.DavMultipartFile? multipartFile)
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
                    // Size unknown, use 0
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
