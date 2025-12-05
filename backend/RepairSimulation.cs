using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV;

public static class RepairSimulation
{
    public static async Task RunAsync()
    {
        Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
        Log.Information("Starting Repair Simulation...");

        // 1. Initialize Config and DB
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);
        
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);

        // 2. Find the Target Item
        var jobName = "1.HAPPY.FAMILY.USA.S01E02.1080p.AMZN.WEB-DL.DDP5.1.H.264-TURG";
        Log.Information($"Searching for job: {jobName}");
        
        var jobFolder = await dbContext.Items
            .FirstOrDefaultAsync(x => x.Name == jobName && x.Type == DavItem.ItemType.Directory)
            .ConfigureAwait(false);

        if (jobFolder == null)
        {
            Log.Error("Job folder not found in DB.");
            return;
        }

        // Find the file inside
        var fileItem = await dbContext.Items
            .FirstOrDefaultAsync(x => x.ParentId == jobFolder.Id && x.Name.EndsWith(".mkv"))
            .ConfigureAwait(false);

        if (fileItem == null)
        {
            Log.Error("Video file not found inside job folder.");
            return;
        }
        
        Log.Information($"Found Virtual File: {fileItem.Name} (ID: {fileItem.Id})");

        // 3. Resolve Physical Path (Symlink)
        var symlinkPath = OrganizedLinksUtil.GetLink(fileItem, configManager);
        if (symlinkPath == null)
        {
            Log.Error("Could not resolve symlink path using OrganizedLinksUtil.");
            return;
        }
        Log.Information($"Resolved Physical Symlink Path: {symlinkPath}");

        // 4. Match against Sonarr
        var arrConfig = configManager.GetArrConfig();
        Log.Information($"Found {arrConfig.SonarrInstances.Count} Sonarr instances.");

        foreach (var instance in arrConfig.SonarrInstances)
        {
            Log.Information($"Checking Sonarr Instance: {instance.Host}");
            var client = new SonarrClient(instance.Host, instance.ApiKey);

            try
            {
                // A. Check Root Folder Mapping
                var rootFolders = await client.GetRootFolders().ConfigureAwait(false);
                var matchedRoot = rootFolders.FirstOrDefault(x => symlinkPath.StartsWith(x.Path!));
                
                if (matchedRoot == null)
                {
                    Log.Warning($"[Mismatch] Symlink path does NOT start with any Root Folder in this Sonarr instance.");
                    Log.Information("Sonarr Root Folders:");
                    foreach (var rf in rootFolders) Log.Information($" - {rf.Path}");
                    
                    Log.Warning("This confirms the issue: NzbDav expects the symlink path to be valid in Sonarr, but Sonarr has likely moved the file or uses a different mount path.");
                    continue;
                }
                
                Log.Information($"[Match] Path matches Root Folder: {matchedRoot.Path}");

                // B. Check Media Matching (Simulation)
                Log.Information("Attempting to find EpisodeFile via API (Dry Run)...");
                var mediaIds = await client.GetMediaIds(symlinkPath).ConfigureAwait(false);

                if (mediaIds == null)
                {
                    Log.Error("Sonarr could not find an EpisodeFile matching this path.");
                }
                else
                {
                    Log.Information($"SUCCESS! Found EpisodeFile ID: {mediaIds.Value.episodeFileId}");
                    Log.Information($"Linked Episode IDs: {string.Join(", ", mediaIds.Value.episodeIds)}");
                    Log.Information("Repair would trigger: DELETE EpisodeFile + EpisodeSearch.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error communicating with Sonarr: {ex.Message}");
            }
        }
    }
}