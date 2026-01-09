using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using System.Text;
using Serilog;

namespace NzbWebDAV.Tools;

public static class ExtractTestNzbs
{
    public static async Task RunAsync(string[] args)
    {
        Log.Information("Extracting test NZBs...");

        var outputDir = "../test_files";
        Directory.CreateDirectory(outputDir);

        await using var db = new DavDatabaseContext();
        var client = new DavDatabaseClient(db);

        // Define criteria
        var minSize = 200 * 1024 * 1024L;
        var maxSize = 1500 * 1024 * 1024L; // 1.5GB roughly

        var candidates = await db.Items
            .AsNoTracking()
            .Where(x => x.FileSize > minSize && x.FileSize < maxSize)
            .Where(x => x.Type == DavItem.ItemType.NzbFile 
                     || x.Type == DavItem.ItemType.RarFile 
                     || x.Type == DavItem.ItemType.MultipartFile)
            .OrderByDescending(x => x.CreatedAt) // Recent files first
            .Take(50) // Fetch more to filter by extension/uniqueness
            .ToListAsync();

        var selected = new List<DavItem>();
        var typesFound = new HashSet<DavItem.ItemType>();

        foreach (var item in candidates)
        {
            if (selected.Count >= 5) break;
            
            // Try to get diversity - prioritize new types if we don't have them yet
            // But if we only have one type available, just fill with that.
            if (selected.Count < 3 && typesFound.Contains(item.Type) && candidates.Any(c => !typesFound.Contains(c.Type) && !selected.Contains(c)))
                continue;

            // Also check extensions?
            selected.Add(item);
            typesFound.Add(item.Type);
        }

        Log.Information($"Found {selected.Count} candidates.");

        foreach (var item in selected)
        {
            Log.Information($"Processing {item.Name} ({item.Type})...");
            string? nzbContent = null;

            // Try History/Queue first (simpler)
             var jobName = Path.GetFileName(Path.GetDirectoryName(item.Path));
             
             // Check Queue
             var qItem = await db.QueueItems.AsNoTracking().FirstOrDefaultAsync(x => x.JobName == jobName);
             if (qItem != null)
             {
                 var qContent = await db.QueueNzbContents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == qItem.Id);
                 nzbContent = qContent?.NzbContents;
             }
             
             // Check History
             if (nzbContent == null)
             {
                 var hItem = await db.HistoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.JobName == jobName);
                 nzbContent = hItem?.NzbContents;
             }

             // Fallback Generation
             if (nzbContent == null)
             {
                 Log.Information("  > Generating NZB from metadata...");
                 var nzbFile = await db.NzbFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.Id);
                 var rarFile = await db.RarFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.Id);
                 var multipartFile = await db.MultipartFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.Id);
                 
                 nzbContent = GenerateNzbXml(item, nzbFile, rarFile, multipartFile);
             }

             if (nzbContent != null)
             {
                 var filename = Path.Combine(outputDir, $"{item.Name}.nzb");
                 await File.WriteAllTextAsync(filename, nzbContent);
                 Log.Information($"  > Saved to {filename}");
             }
             else
             {
                 Log.Warning($"  > Could not find or generate NZB for {item.Name}");
             }
        }
        
        Log.Information("Done.");
    }
    
    // Copied from DownloadNzbController
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
