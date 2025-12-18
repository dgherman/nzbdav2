using System.Net;
using Serilog;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class SonarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly Dictionary<string, int> SeriesPathToSeriesIdCache = new();
    private static readonly Dictionary<string, int> SymlinkOrStrmToEpisodeFileIdCache = new();

    public Task<SonarrQueue> GetSonarrQueueAsync() =>
        Get<SonarrQueue>($"/queue?protocol=usenet&pageSize=5000");

    public Task<List<SonarrSeries>> GetAllSeries() =>
        Get<List<SonarrSeries>>($"/series");

    public Task<SonarrSeries> GetSeries(int seriesId) =>
        Get<SonarrSeries>($"/series/{seriesId}");

    public Task<SonarrEpisodeFile> GetEpisodeFile(int episodeFileId) =>
        Get<SonarrEpisodeFile>($"/episodefile/{episodeFileId}");

    public Task<List<SonarrEpisodeFile>> GetAllEpisodeFiles(int seriesId) =>
        Get<List<SonarrEpisodeFile>>($"/episodefile?seriesId={seriesId}");

    public Task<List<SonarrEpisode>> GetEpisodesFromEpisodeFileId(int episodeFileId) =>
        Get<List<SonarrEpisode>>($"/episode?episodeFileId={episodeFileId}");

    public Task<HttpStatusCode> DeleteEpisodeFile(int episodeFileId) =>
        Delete($"/episodefile/{episodeFileId}");

    public Task<ArrCommand> SearchEpisodesAsync(List<int> episodeIds) =>
        CommandAsync(new { name = "EpisodeSearch", episodeIds });

    public override Task<ArrHistory> GetHistoryAsync(int? movieId = null, int? seriesId = null)
    {
        var query = "";
        // Sonarr history endpoint uses seriesIds (plural)
        if (seriesId.HasValue) query = $"?seriesIds={seriesId.Value}&eventType={(int)ArrEventType.Grabbed}";
        // movieId is not applicable for Sonarr history endpoint
        return Get<ArrHistory>($"/history{query}");
    }

    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath)
    {
        Log.Information($"[ArrClient] Attempting to remove and search for '{symlinkOrStrmPath}' in Sonarr '{Host}'");

        // get episode-file-id and episode-ids
        var mediaIds = await GetMediaIds(symlinkOrStrmPath);
        if (mediaIds == null)
        {
            Log.Warning($"[ArrClient] Could not find media IDs for '{symlinkOrStrmPath}' in Sonarr. Aborting RemoveAndSearch.");
            return false;
        }

        // 1. Get Scene Name (Original Release Name)
        var episodeFile = await GetEpisodeFile(mediaIds.Value.episodeFileId);
        var sceneName = episodeFile.SceneName;
        var seriesId = episodeFile.SeriesId;
        Log.Debug($"[ArrClient] Found episode file (ID: {episodeFile.Id}, Series ID: {seriesId}). SceneName: '{sceneName}'");

        // 2. Delete the episode-file
        Log.Information($"[ArrClient] Deleting episode file ID {mediaIds.Value.episodeFileId} from Sonarr...");
        if (await DeleteEpisodeFile(mediaIds.Value.episodeFileId) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete episode file `{symlinkOrStrmPath}` from sonarr instance `{Host}`.");
        
        Log.Information($"[ArrClient] Successfully deleted episode file ID {mediaIds.Value.episodeFileId}.");

        // 3. Try to find the "grab" event in history and mark it as failed (this handles blacklist + search)
        if (!string.IsNullOrEmpty(sceneName))
        {
            Log.Debug($"[ArrClient] Searching history for grab event with source title '{sceneName}'...");
            try
            {
                var history = await GetHistoryAsync(seriesId: seriesId);
                var grabEvent = history.Records
                    .FirstOrDefault(x => 
                        x.SourceTitle != null &&
                        x.SourceTitle.Equals(sceneName, StringComparison.OrdinalIgnoreCase) &&
                        x.Data != null &&
                        x.Data.TryGetValue("protocol", out var protocol) &&
                        protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
                    );
                
                if (grabEvent != null)
                {
                    Log.Information($"[ArrClient] Found grab event ID {grabEvent.Id}. Attempting to mark as failed...");
                    var markFailedResult = await MarkHistoryFailedAsync(grabEvent.Id);
                    if (markFailedResult)
                    {
                        Log.Information($"[ArrClient] Successfully marked history item {grabEvent.Id} as failed for '{sceneName}' in Sonarr '{Host}'.");
                        return true;
                    }
                    else
                    {
                        Log.Warning($"[ArrClient] Failed to mark history item {grabEvent.Id} as failed for '{sceneName}' in Sonarr '{Host}'. Proceeding to fallback search.");
                    }
                }
                else
                {
                    Log.Warning($"[ArrClient] Could not find grab event in history for '{sceneName}' in Sonarr '{Host}'. Proceeding to fallback search.");
                    
                    // Detailed logging for diagnostics
                    if (history?.Records != null && history.Records.Count > 0)
                    {
                        Log.Debug($"[ArrClient] Fetched {history.Records.Count} history records. Top 5 records: {string.Join(", ", history.Records.Take(5).Select(r => $"'{r.SourceTitle}' ({r.EventType})"))}");
                    }
                    else
                    {
                        Log.Debug("[ArrClient] History API returned 0 records.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ArrClient] Error while attempting to mark history item as failed for '{sceneName}' in Sonarr '{Host}': {ex.Message}. Proceeding to fallback search.");
            }
        }
        else
        {
            Log.Warning($"[ArrClient] SceneName was null or empty for file. Cannot perform history lookup/blacklist. Proceeding to fallback search.");
        }

        // 4. Fallback: Trigger a new search for each episode
        Log.Information($"[ArrClient] Triggering fallback search for {mediaIds.Value.episodeIds.Count} episode(s)...");
        await SearchEpisodesAsync(mediaIds.Value.episodeIds);
        return true;
    }

    public async Task<(int episodeFileId, List<int> episodeIds)?> GetMediaIds(string symlinkOrStrmPath)
    {
        // get episode-file-id
        var episodeFileId = await GetEpisodeFileId(symlinkOrStrmPath);
        if (episodeFileId == null)
        {
            Log.Debug($"[ArrClient] Could not find Episode File ID for '{symlinkOrStrmPath}'.");
            return null;
        }

        // get episode-ids
        var episodes = await GetEpisodesFromEpisodeFileId(episodeFileId.Value);
        var episodeIds = episodes.Select(x => x.Id).ToList();
        if (episodeIds.Count == 0)
        {
            Log.Debug($"[ArrClient] Found Episode File ID {episodeFileId} but no associated episodes.");
            return null;
        }

        // return
        Log.Debug($"[ArrClient] Found match: File ID {episodeFileId}, Episode IDs: {string.Join(",", episodeIds)}");
        return (episodeFileId.Value, episodeIds);
    }

    public async Task<int?> GetEpisodeFileId(string symlinkOrStrmPath)
    {
        // if episode-file-id is found in the cache, verify it and return it
        if (SymlinkOrStrmToEpisodeFileIdCache.TryGetValue(symlinkOrStrmPath, out var episodeFileId))
        {
            var episodeFile = await GetEpisodeFile(episodeFileId);
            if (episodeFile.Path == symlinkOrStrmPath) return episodeFileId;
        }

        // otherwise, find the series-id
        var seriesId = await GetSeriesId(symlinkOrStrmPath);
        if (seriesId == null) return null;

        // then use it to find all episode-files and repopulate the cache
        int? result = null;
        foreach (var episodeFile in await GetAllEpisodeFiles(seriesId.Value))
        {
            SymlinkOrStrmToEpisodeFileIdCache[episodeFile.Path!] = episodeFile.Id;
            if (episodeFile.Path == symlinkOrStrmPath)
                result = episodeFile.Id;
        }

        // return the found episode-file-id
        return result;
    }

    public async Task<int?> GetSeriesId(string symlinkOrStrmPath)
    {
        // get series-id from cache
        var cachedSeriesId = PathUtil.GetAllParentDirectories(symlinkOrStrmPath)
            .Where(x => SeriesPathToSeriesIdCache.ContainsKey(x))
            .Select(x => SeriesPathToSeriesIdCache[x])
            .Select(x => (int?)x)
            .FirstOrDefault();

        // if found, verify and return it
        if (cachedSeriesId != null)
        {
            var series = await GetSeries(cachedSeriesId.Value);
            if (symlinkOrStrmPath.StartsWith(series.Path!))
                return cachedSeriesId;
        }

        // otherwise, fetch all series and repopulate the cache
        int? result = null;
        var allSeries = await GetAllSeries();
        
        // 1. Try Strict Match
        foreach (var series in allSeries)
        {
            if (series.Path != null)
            {
                SeriesPathToSeriesIdCache[series.Path] = series.Id;
                if (symlinkOrStrmPath.StartsWith(series.Path))
                {
                    result = series.Id;
                    break;
                }
            }
        }

        // 2. Fallback: Folder Name Match (if paths are mapped differently)
        if (result == null)
        {
            foreach (var series in allSeries)
            {
                if (series.Path != null)
                {
                    var seriesFolderName = Path.GetFileName(series.Path.TrimEnd('/'));
                    if (!string.IsNullOrEmpty(seriesFolderName) && 
                        symlinkOrStrmPath.Contains($"/{seriesFolderName}/", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Debug($"[ArrClient] Found potential series match by folder name for '{symlinkOrStrmPath}': '{series.Path}'");
                        result = series.Id;
                        break;
                    }
                }
            }
        }

        // return the found series-id
        return result;
    }
}