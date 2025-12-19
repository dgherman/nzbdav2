using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr;
using Serilog;

namespace NzbWebDAV.Tools;

public static class ArrHistoryTester
{
    public static async Task RunAsync(string[] args)
    {
        // Expected args: --test-arr-history <type> <host> <apiKey> <filePath>
        // type: sonarr or radarr
        
        if (args.Length < 5)
        {
            Console.WriteLine("Usage: --test-arr-history <type> <host> <apiKey> <filePath>");
            return;
        }

        var type = args[1].ToLower();
        var host = args[2];
        var apiKey = args[3];
        var filePath = args[4];

        Log.Information($"Starting ArrHistoryTester for {type} at {host} with file '{filePath}'");

        try
        {
            if (type == "sonarr")
            {
                await RunSonarrAsync(host, apiKey, filePath);
            }
            else if (type == "radarr")
            {
                await RunRadarrAsync(host, apiKey, filePath);
            }
            else
            {
                throw new ArgumentException("Invalid type. Must be 'sonarr' or 'radarr'.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred during testing.");
        }
    }

    private static async Task RunSonarrAsync(string host, string apiKey, string filePath)
    {
        var client = new SonarrClient(host, apiKey);

        // 1. Get Media IDs
        Log.Information($"[Sonarr] Resolving media IDs for path: {filePath}...");
        var mediaIds = await client.GetMediaIds(filePath);
        if (mediaIds == null)
        {
            Log.Error("[Sonarr] Could not find media IDs for the given path. Check if the path is correct and visible to Sonarr.");
            return;
        }
        
        var (episodeFileId, episodeIds) = mediaIds.Value;
        Log.Information($"[Sonarr] Found EpisodeFileId: {episodeFileId}, EpisodeIds: {string.Join(",", episodeIds)}");

        // 2. Get Episode File (for SceneName and SeriesId)
        Log.Information($"[Sonarr] Fetching episode file details for ID {episodeFileId}...");
        var episodeFile = await client.GetEpisodeFile(episodeFileId);
        var sceneName = episodeFile.SceneName;
        var seriesId = episodeFile.SeriesId;
        
        Log.Information($"[Sonarr] SceneName: '{sceneName}', SeriesId: {seriesId}");

        if (string.IsNullOrEmpty(sceneName))
        {
            Log.Error("[Sonarr] SceneName is null or empty. Cannot search history.");
            return;
        }

        // 3. Search History
        int pageSize = 1000;
        Log.Information($"[Sonarr] Searching history for SeriesId {seriesId} (PageSize: {pageSize})...");
        var history = await client.GetHistoryAsync(seriesId: seriesId, pageSize: pageSize);
        
        Log.Information($"[Sonarr] Fetched {history.Records.Count} history records.");

        // 4. Filter History
        Log.Information($"[Sonarr] Filtering for SourceTitle: '{sceneName}' and Protocol: 'usenet'...");
        var grabEvent = history.Records.FirstOrDefault(x => 
            x.SourceTitle != null &&
            x.SourceTitle.Equals(sceneName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.EventType, "grabbed", StringComparison.OrdinalIgnoreCase) &&
            x.Data != null &&
            x.Data.TryGetValue("protocol", out var protocol) &&
            protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
        );

        if (grabEvent != null)
        {
            Log.Information($"[Sonarr] MATCH FOUND! History ID: {grabEvent.Id}, Title: {grabEvent.SourceTitle}");
            Log.Information($"Event Data: {JsonSerializer.Serialize(grabEvent, new JsonSerializerOptions { WriteIndented = true })}");

            // 5. Blacklist
            Log.Information("[Sonarr] Attempting to mark history as failed (Blacklist)...");
            var success = await client.MarkHistoryFailedAsync(grabEvent.Id);
            if (success) Log.Information("[Sonarr] SUCCESS: History item marked as failed.");
            else Log.Error("[Sonarr] FAILURE: Failed to mark history item as failed.");
        }
        else
        {
            Log.Warning("[Sonarr] NO MATCH FOUND in history.");
            // Dump top 5
            Log.Information("Top 5 History Records for context:");
            foreach (var record in history.Records.Take(5))
            {
                 Log.Information($" - [{record.Id}] {record.SourceTitle} ({record.EventType})");
            }
        }
    }

    private static async Task RunRadarrAsync(string host, string apiKey, string filePath)
    {
        var client = new RadarrClient(host, apiKey);

        // 1. Get Media IDs
        Log.Information($"[Radarr] Resolving media IDs for path: {filePath}...");
        var mediaIds = await client.GetMediaIds(filePath);
        if (mediaIds == null)
        {
            Log.Error("[Radarr] Could not find media IDs for the given path. Check if the path is correct and visible to Radarr.");
            return;
        }

        var (movieFileId, movieId) = mediaIds.Value;
        Log.Information($"[Radarr] Found MovieFileId: {movieFileId}, MovieId: {movieId}");

        // 2. Get Movie (for SceneName)
        Log.Information($"[Radarr] Fetching movie details for ID {movieId}...");
        var movie = await client.GetMovieAsync(movieId);
        var sceneName = movie.MovieFile?.SceneName;
        
        Log.Information($"[Radarr] SceneName: '{sceneName}'");

        if (string.IsNullOrEmpty(sceneName))
        {
            Log.Error("[Radarr] SceneName is null or empty. Cannot search history.");
            return;
        }

        // 3. Search History
        int pageSize = 1000;
        Log.Information($"[Radarr] Searching history for MovieId {movieId} (PageSize: {pageSize})...");
        var history = await client.GetHistoryAsync(movieId: movieId, pageSize: pageSize);
        
        Log.Information($"[Radarr] Fetched {history.Records.Count} history records.");

        // 4. Filter History
        Log.Information($"[Radarr] Filtering for SourceTitle: '{sceneName}' and Protocol: 'usenet'...");
        var grabEvent = history.Records.FirstOrDefault(x => 
            x.SourceTitle != null &&
            x.SourceTitle.Equals(sceneName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.EventType, "grabbed", StringComparison.OrdinalIgnoreCase) &&
            x.Data != null &&
            x.Data.TryGetValue("protocol", out var protocol) &&
            protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
        );

        if (grabEvent != null)
        {
            Log.Information($"[Radarr] MATCH FOUND! History ID: {grabEvent.Id}, Title: {grabEvent.SourceTitle}");
            Log.Information($"Event Data: {JsonSerializer.Serialize(grabEvent, new JsonSerializerOptions { WriteIndented = true })}");

            // 5. Blacklist
            Log.Information("[Radarr] Attempting to mark history as failed (Blacklist)...");
            var success = await client.MarkHistoryFailedAsync(grabEvent.Id);
            if (success) Log.Information("[Radarr] SUCCESS: History item marked as failed.");
            else Log.Error("[Radarr] FAILURE: Failed to mark history item as failed.");
        }
        else
        {
            Log.Warning("[Radarr] NO MATCH FOUND in history.");
            Log.Information("Top 5 History Records for context:");
            foreach (var record in history.Records.Take(5))
            {
                 Log.Information($" - [{record.Id}] {record.SourceTitle} ({record.EventType})");
            }
        }
    }
}