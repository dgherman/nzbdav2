using System.Net;
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
        var mediaIds = await GetMediaIds(symlinkOrStrmPath);
        if (mediaIds == null) return false;

        // 1. Get Scene Name (Original Release Name) from the file before deleting
        var movie = await GetMovieAsync(mediaIds.Value.movieId);
        var sceneName = movie.MovieFile?.SceneName;

        // 2. Delete the file
        if (await DeleteMovieFile(mediaIds.Value.movieFileId) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete movie file `{symlinkOrStrmPath}` from radarr instance `{Host}`.");

        // 3. Try to find the "grab" event in history and mark it as failed (this handles blacklist + search)
        if (!string.IsNullOrEmpty(sceneName))
        {
            var history = await GetHistoryAsync(movieId: mediaIds.Value.movieId);
            var grabEvent = history.Records
                .FirstOrDefault(x => 
                    x.SourceTitle.Equals(sceneName, StringComparison.OrdinalIgnoreCase) &&
                    x.Data.TryGetValue("protocol", out var protocol) &&
                    protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
                );
            
            if (grabEvent != null)
            {
                if (await MarkHistoryFailedAsync(grabEvent.Id))
                {
                    return true;
                }
            }
        }

        // 4. Fallback: Just trigger a standard search if history lookup failed
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
                return (movie.MovieFile.Id!, movieId);
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie-id and movie-file-id
        var allMovies = await GetMoviesAsync();
        (int movieFileId, int movieId)? result = null;
        foreach (var movie in allMovies)
        {
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
                SymlinkOrStrmToMovieIdCache[movieFile.Path] = movie.Id;
            if (movieFile?.Path == symlinkOrStrmPath)
                result = (movieFile.Id!, movie.Id);
        }

        return result;
    }
}