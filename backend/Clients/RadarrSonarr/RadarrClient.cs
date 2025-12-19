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


    public override Task<bool> RemoveAndSearch(string symlinkOrStrmPath, int? episodeId = null, string sortKey = "date", string sortAtrection = "descending") =>
        Task.FromResult(false);

    public async Task<(int movieFileId, int movieId)?> GetMediaIds(string symlinkOrStrmPath)
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