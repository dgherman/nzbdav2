using System.Net;
using System.Linq;
using Serilog;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class RadarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly Dictionary<string, int> SymlinkOrStrmToMovieIdCache = new();

    public Task<RadarrMovie> GetMovieAsync(int id) =>
        Get<RadarrMovie>($"/movie/{id}");

    public Task<List<RadarrMovie>> GetMoviesAsync() =>
        Get<List<RadarrMovie>>($"/movie");

    public Task<RadarrQueue> GetRadarrQueueAsync() =>
        Get<RadarrQueue>($"/queue?protocol=usenet&pageSize=5000");

    public Task<HttpStatusCode> DeleteMovieFile(int id) =>
        Delete($"/moviefile/{id}", new Dictionary<string, string> { ["deleteFiles"] = "true" });

    public Task<ArrCommand> SearchMovieAsync(int id) =>
        CommandAsync(new { name = "MoviesSearch", movieIds = new List<int> { id } });


    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath, int? episodeId = null, string sortKey = "date", string sortDirection = "descending")
    {
        Log.Information($"[ArrClient] Attempting to remove and search for '{symlinkOrStrmPath}' in Radarr '{Host}'");

        var mediaIds = await GetMediaIds(symlinkOrStrmPath);
        if (mediaIds == null)
        {
            Log.Warning($"[ArrClient] Could not find media IDs for '{symlinkOrStrmPath}' in Radarr. Aborting RemoveAndSearch.");
            return false;
        }

        // 1. Get scene name before deletion for history lookup
        var movie = await GetMovieAsync(mediaIds.Value.movieId);
        var sceneName = movie.MovieFile?.SceneName;

        // 2. Delete the movie file
        Log.Information($"[ArrClient] Deleting movie file ID {mediaIds.Value.movieFileId} from Radarr...");
        if (await DeleteMovieFile(mediaIds.Value.movieFileId) != System.Net.HttpStatusCode.OK)
            throw new Exception($"Failed to delete movie file '{symlinkOrStrmPath}' from Radarr instance '{Host}'.");

        Log.Information($"[ArrClient] Successfully deleted movie file ID {mediaIds.Value.movieFileId}.");

        // 3. Try to find the grab event in history and mark it failed so Arr blocklists this release.
        // Always trigger an explicit search afterward because Arr's failed-download handling may not auto-search.
        if (!string.IsNullOrEmpty(sceneName))
        {
            try
            {
                var history = await GetHistoryAsync(movieId: mediaIds.Value.movieId, sortKey: sortKey, sortDirection: sortDirection);
                var grabEvent = history.Records
                    .FirstOrDefault(x =>
                        x.SourceTitle != null &&
                        x.SourceTitle.Equals(sceneName, StringComparison.OrdinalIgnoreCase) &&
                        x.Data != null &&
                        x.Data.TryGetValue("protocol", out var protocol) &&
                        protocol == "1" // 1 = usenet
                    );

                if (grabEvent != null)
                {
                    Log.Information($"[ArrClient] Found grab event ID {grabEvent.Id}. Attempting to mark as failed...");
                    if (await MarkHistoryFailedAsync(grabEvent.Id))
                    {
                        Log.Information($"[ArrClient] Successfully marked history item {grabEvent.Id} as failed for '{sceneName}' in Radarr '{Host}'. Triggering explicit replacement search.");
                    }
                    else
                    {
                        Log.Warning($"[ArrClient] Failed to mark history item {grabEvent.Id} as failed. Proceeding to replacement search.");
                    }
                }
                else
                {
                    Log.Warning($"[ArrClient] Could not find grab event in history for '{sceneName}' in Radarr '{Host}'. Proceeding to replacement search.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ArrClient] Error while attempting to mark history failed for '{sceneName}' in Radarr '{Host}': {ex.Message}. Proceeding to replacement search.");
            }
        }
        else
        {
            Log.Warning($"[ArrClient] SceneName was null or empty for movie file. Proceeding to replacement search.");
        }

        // 4. Trigger a new movie search
        Log.Information($"[ArrClient] Triggering replacement search for movie ID {mediaIds.Value.movieId}...");
        await SearchMovieAsync(mediaIds.Value.movieId);
        return true;
    }

    public async Task<(int movieFileId, int movieId)?> GetMediaIds(string symlinkOrStrmPath)
    {
        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkOrStrmToMovieIdCache.TryGetValue(symlinkOrStrmPath, out var movieId))
        {
            var movie = await GetMovieAsync(movieId);
            if (movie.MovieFile?.Path == symlinkOrStrmPath)
            {
                return (movie.MovieFile.Id!, movieId);
            }
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie-id and movie-file-id
        var allMovies = await GetMoviesAsync();
        (int movieFileId, int movieId)? result = null;
        var fileName = Path.GetFileName(symlinkOrStrmPath);

        foreach (var movie in allMovies)
        {
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
            {
                SymlinkOrStrmToMovieIdCache[movieFile.Path] = movie.Id;
                
                if (movieFile.Path == symlinkOrStrmPath)
                {
                    result = (movieFile.Id!, movie.Id);
                    break; // Strict match found, stop searching
                }
                
                if (result == null && Path.GetFileName(movieFile.Path) == fileName)
                {
                    // Fallback match, keep searching in case we find a strict match later
                    result = (movieFile.Id!, movie.Id);
                }
            }
        }

        if (result == null) Log.Warning($"[ArrClient] No match found for '{symlinkOrStrmPath}' after checking {allMovies.Count} movies.");
        return result;
    }
}