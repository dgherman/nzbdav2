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
        Delete($"/moviefile/{id}");

    public Task<ArrCommand> SearchMovieAsync(int id) =>
        CommandAsync(new { name = "MoviesSearch", movieIds = new List<int> { id } });


    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath)
    {
        Log.Information($"[ArrClient] Attempting to remove and search for '{symlinkOrStrmPath}' in Radarr '{Host}'");
        
        var mediaIds = await GetMediaIds(symlinkOrStrmPath);
        if (mediaIds == null)
        {
            Log.Warning($"[ArrClient] Could not find media IDs for '{symlinkOrStrmPath}' in Radarr. Aborting RemoveAndSearch.");
            return false;
        }

        // 1. Get Scene Name (Original Release Name) from the file before deleting
        var movie = await GetMovieAsync(mediaIds.Value.movieId);
        var sceneName = movie.MovieFile?.SceneName;
        Log.Debug($"[ArrClient] Found movie '{movie.Title}' (ID: {movie.Id}). File SceneName: '{sceneName}'");

        // 2. Delete the file
        Log.Information($"[ArrClient] Deleting movie file ID {mediaIds.Value.movieFileId} from Radarr...");
        if (await DeleteMovieFile(mediaIds.Value.movieFileId) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete movie file `{symlinkOrStrmPath}` from radarr instance `{Host}`.");
        
        Log.Information($"[ArrClient] Successfully deleted movie file ID {mediaIds.Value.movieFileId}.");

        // 3. Try to find the "grab" event in history and mark it as failed (this handles blacklist + search)
        if (!string.IsNullOrEmpty(sceneName))
        {
            Log.Debug($"[ArrClient] Searching history for grab event with source title '{sceneName}'...");
            try
            {
                var history = await GetHistoryAsync(movieId: mediaIds.Value.movieId);
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
                        Log.Information($"[ArrClient] Successfully marked history item {grabEvent.Id} as failed for '{sceneName}' in Radarr '{Host}'.");
                        return true;
                    }
                    else
                    {
                        Log.Warning($"[ArrClient] Failed to mark history item {grabEvent.Id} as failed for '{sceneName}' in Radarr '{Host}'. Proceeding to fallback search.");
                    }
                }
                else
                {
                    Log.Warning($"[ArrClient] Could not find grab event in history for '{sceneName}' in Radarr '{Host}'. Proceeding to fallback search.");
                    
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
                Log.Warning($"[ArrClient] Error while attempting to mark history item as failed for '{sceneName}' in Radarr '{Host}': {ex.Message}. Proceeding to fallback search.");
            }
        }
        else
        {
            Log.Warning($"[ArrClient] SceneName was null or empty for file. Cannot perform history lookup/blacklist. Proceeding to fallback search.");
        }

        // 4. Fallback: Just trigger a standard search if history lookup failed
        Log.Information($"[ArrClient] Triggering fallback search for Movie ID {mediaIds.Value.movieId}...");
        await SearchMovieAsync(mediaIds.Value.movieId);
        return true;
    }

    private async Task<(int movieFileId, int movieId)?> GetMediaIds(string symlinkOrStrmPath)
    {
        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkOrStrmToMovieIdCache.TryGetValue(symlinkOrStrmPath, out var movieId))
        {
            var movie = await GetMovieAsync(movieId);
            if (movie.MovieFile?.Path == symlinkOrStrmPath)
            {
                Log.Debug($"[ArrClient] Cache hit for '{symlinkOrStrmPath}'. Movie ID: {movieId}, File ID: {movie.MovieFile.Id}");
                return (movie.MovieFile.Id!, movieId);
            }
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie-id and movie-file-id
        Log.Debug($"[ArrClient] Fetching all movies to match '{symlinkOrStrmPath}'...");
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
                    Log.Debug($"[ArrClient] Strict match found. Movie ID: {movie.Id}, File ID: {movieFile.Id}");
                    break; // Strict match found, stop searching
                }
                
                if (result == null && Path.GetFileName(movieFile.Path) == fileName)
                {
                    // Fallback match, keep searching in case we find a strict match later
                    Log.Debug($"[ArrClient] Found potential match by filename for '{symlinkOrStrmPath}': '{movieFile.Path}' (Movie ID: {movie.Id})");
                    result = (movieFile.Id!, movie.Id);
                }
            }
        }

        if (result == null) Log.Warning($"[ArrClient] No match found for '{symlinkOrStrmPath}' after checking {allMovies.Count} movies.");
        return result;
    }
}