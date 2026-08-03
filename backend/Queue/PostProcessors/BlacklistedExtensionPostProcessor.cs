using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.PostProcessors;

public class BlacklistedExtensionPostProcessor(ConfigManager configManager, DavDatabaseClient dbClient)
{
    public void RemoveFilteredFiles()
    {
        var addedFiles = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .ToList();

        foreach (var (file, reason) in GetFilesToRemove(addedFiles))
        {
            Log.Information("Filtering out {FileName} ({Reason}).", file.Name, reason);
            RemoveFile(file, reason);
        }
    }

    private IEnumerable<(DavItem File, string Reason)> GetFilesToRemove(IReadOnlyCollection<DavItem> addedFiles)
    {
        var blacklistedExtensions = configManager.GetBlacklistedExtensions();
        var blacklistedFilenames = configManager.GetBlacklistedFilenamePatterns();
        var sampleFilterEnabled = configManager.IsSampleFilterEnabled();

        // the sample heuristic compares each candidate against the largest video
        // in the same release, so the largest video can never be a sample itself.
        var largestVideoFileSize = sampleFilterEnabled
            ? addedFiles
                .Where(x => FilenameUtil.IsVideoFile(x.Name))
                .Max(x => x.FileSize ?? 0)
            : 0;

        foreach (var file in addedFiles)
        {
            if (blacklistedExtensions.Contains(Path.GetExtension(file.Name).ToLower()))
                yield return (file, "blacklisted extension");

            else if (FileFilterUtil.MatchesAnyGlob(file.Name, blacklistedFilenames))
                yield return (file, "blacklisted filename");

            else if (sampleFilterEnabled && FileFilterUtil.IsSampleFile(file.Name, file.FileSize, largestVideoFileSize))
                yield return (file, "sample file");
        }
    }

    private void RemoveFile(DavItem davItem, string reason)
    {
        if (davItem.Type == DavItem.ItemType.NzbFile)
        {
            var file = dbClient.Ctx.ChangeTracker.Entries<DavNzbFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .First(x => x.Id == davItem.Id);
            dbClient.Ctx.NzbFiles.Remove(file);
        }

        else if (davItem.Type == DavItem.ItemType.MultipartFile)
        {
            var file = dbClient.Ctx.ChangeTracker.Entries<DavMultipartFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .First(x => x.Id == davItem.Id);
            dbClient.Ctx.MultipartFiles.Remove(file);
        }

        else
        {
            Log.Error("Error filtering {FileName} ({Reason}) from downloading.", davItem.Name, reason);
            return;
        }

        dbClient.Ctx.Items.Remove(davItem);
    }
}
