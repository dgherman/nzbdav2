using System.Text.RegularExpressions;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

public partial class ArrReplacementSearchService(ConfigManager configManager)
{
    public async Task NotifyQueueItemFailedAsync(Guid queueItemId, string jobName, string reason, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;

        var handled = false;
        foreach (var arrClient in configManager.GetArrConfig().GetArrClients())
        {
            try
            {
                handled |= arrClient switch
                {
                    SonarrClient sonarrClient => await NotifySonarrQueueItemFailedAsync(sonarrClient, queueItemId, jobName).ConfigureAwait(false),
                    RadarrClient radarrClient => await NotifyRadarrQueueItemFailedAsync(radarrClient, queueItemId, jobName).ConfigureAwait(false),
                    _ => false
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ArrReplacement] Failed to notify Arr instance {Host} that queue item {JobName} failed: {Reason}",
                    arrClient.Host, jobName, reason);
            }
        }

        if (!handled)
        {
            Log.Warning("[ArrReplacement] No Arr instance accepted failed queue item {JobName} ({QueueItemId}); replacement search could not be forced. Reason: {Reason}",
                jobName, queueItemId, reason);
        }
    }

    public async Task NotifyQueueFilesDeletedAsync(Guid queueItemId, string jobName, IReadOnlyCollection<string> deletedFileNames, string reason, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || deletedFileNames.Count == 0) return;

        var mediaFileNames = deletedFileNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mediaFileNames.Count == 0) return;

        var handled = false;
        foreach (var arrClient in configManager.GetArrConfig().GetArrClients())
        {
            try
            {
                handled |= arrClient switch
                {
                    SonarrClient sonarrClient => await NotifySonarrQueueFilesDeletedAsync(sonarrClient, queueItemId, jobName, mediaFileNames).ConfigureAwait(false),
                    RadarrClient radarrClient => await NotifyRadarrQueueFilesDeletedAsync(radarrClient, queueItemId, jobName).ConfigureAwait(false),
                    _ => false
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ArrReplacement] Failed to notify Arr instance {Host} about {Count} deleted queue file(s) in {JobName}: {Reason}",
                    arrClient.Host, mediaFileNames.Count, jobName, reason);
            }
        }

        if (!handled)
        {
            Log.Warning("[ArrReplacement] No Arr instance accepted deleted queue file(s) for {JobName} ({QueueItemId}); replacement search could not be forced. Files: {Files}. Reason: {Reason}",
                jobName, queueItemId, string.Join(", ", mediaFileNames), reason);
        }
    }

    private async Task<bool> NotifySonarrQueueItemFailedAsync(SonarrClient client, Guid queueItemId, string jobName)
    {
        var queueRecord = await FindSonarrQueueRecordAsync(client, queueItemId, jobName).ConfigureAwait(false);
        if (queueRecord != null && await DeleteArrQueueRecordAsync(client, queueRecord, jobName).ConfigureAwait(false))
        {
            Log.Information("[ArrReplacement] Sonarr {Host} removed/blocklisted failed queue item {JobName} and requested a replacement search.",
                client.Host, jobName);
            return true;
        }

        var historyRecords = await GetMatchingHistoryRecordsAsync(client, queueItemId, jobName).ConfigureAwait(false);
        var episodeIds = historyRecords
            .Select(x => x.EpisodeId)
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (episodeIds.Count == 0 && queueRecord != null)
            episodeIds = await ResolveSonarrQueueEpisodeIdsAsync(client, queueRecord, []).ConfigureAwait(false);

        if (episodeIds.Count == 0) return false;

        await MarkHistoryRecordsFailedAsync(client, historyRecords, jobName).ConfigureAwait(false);
        await client.SearchEpisodesAsync(episodeIds).ConfigureAwait(false);
        Log.Information("[ArrReplacement] Sonarr {Host} searched {Count} episode(s) after failed queue item {JobName}.",
            client.Host, episodeIds.Count, jobName);
        return true;
    }

    private async Task<bool> NotifySonarrQueueFilesDeletedAsync(SonarrClient client, Guid queueItemId, string jobName, IReadOnlyCollection<string> fileNames)
    {
        var queueRecord = await FindSonarrQueueRecordAsync(client, queueItemId, jobName).ConfigureAwait(false);
        var historyRecords = await GetMatchingHistoryRecordsAsync(client, queueItemId, jobName).ConfigureAwait(false);
        var episodeIds = queueRecord != null
            ? await ResolveSonarrQueueEpisodeIdsAsync(client, queueRecord, fileNames).ConfigureAwait(false)
            : [];

        if (episodeIds.Count == 0)
        {
            var seriesIds = historyRecords
                .Select(x => x.SeriesId)
                .Where(x => x > 0)
                .Distinct()
                .ToList();
            episodeIds = await ResolveSonarrEpisodeIdsFromSeriesAsync(client, seriesIds, fileNames).ConfigureAwait(false);
        }

        if (episodeIds.Count == 0)
        {
            episodeIds = historyRecords
                .Select(x => x.EpisodeId)
                .Where(x => x > 0)
                .Distinct()
                .ToList();
        }

        if (episodeIds.Count == 0) return false;

        await MarkHistoryRecordsFailedAsync(client, historyRecords, jobName).ConfigureAwait(false);
        await client.SearchEpisodesAsync(episodeIds).ConfigureAwait(false);
        Log.Information("[ArrReplacement] Sonarr {Host} searched {Count} episode(s) after queue validation deleted file(s) from {JobName}: {Files}",
            client.Host, episodeIds.Count, jobName, string.Join(", ", fileNames));
        return true;
    }

    private async Task<bool> NotifyRadarrQueueItemFailedAsync(RadarrClient client, Guid queueItemId, string jobName)
    {
        var queueRecord = await FindRadarrQueueRecordAsync(client, queueItemId, jobName).ConfigureAwait(false);
        if (queueRecord != null && await DeleteArrQueueRecordAsync(client, queueRecord, jobName).ConfigureAwait(false))
        {
            Log.Information("[ArrReplacement] Radarr {Host} removed/blocklisted failed queue item {JobName} and requested a replacement search.",
                client.Host, jobName);
            return true;
        }

        var historyRecords = await GetMatchingHistoryRecordsAsync(client, queueItemId, jobName).ConfigureAwait(false);
        var movieId = historyRecords.Select(x => x.MovieId).FirstOrDefault(x => x > 0);
        if (movieId <= 0 && queueRecord is { MovieId: > 0 }) movieId = queueRecord.MovieId;
        if (movieId <= 0) return false;

        await MarkHistoryRecordsFailedAsync(client, historyRecords, jobName).ConfigureAwait(false);
        await client.SearchMovieAsync(movieId).ConfigureAwait(false);
        Log.Information("[ArrReplacement] Radarr {Host} searched movie {MovieId} after failed queue item {JobName}.",
            client.Host, movieId, jobName);
        return true;
    }

    private async Task<bool> NotifyRadarrQueueFilesDeletedAsync(RadarrClient client, Guid queueItemId, string jobName)
    {
        var historyRecords = await GetMatchingHistoryRecordsAsync(client, queueItemId, jobName).ConfigureAwait(false);
        var movieId = historyRecords.Select(x => x.MovieId).FirstOrDefault(x => x > 0);
        if (movieId <= 0)
        {
            var queueRecord = await FindRadarrQueueRecordAsync(client, queueItemId, jobName).ConfigureAwait(false);
            if (queueRecord is { MovieId: > 0 }) movieId = queueRecord.MovieId;
        }

        if (movieId <= 0) return false;

        await MarkHistoryRecordsFailedAsync(client, historyRecords, jobName).ConfigureAwait(false);
        await client.SearchMovieAsync(movieId).ConfigureAwait(false);
        Log.Information("[ArrReplacement] Radarr {Host} searched movie {MovieId} after queue validation deleted file(s) from {JobName}.",
            client.Host, movieId, jobName);
        return true;
    }

    private static async Task<bool> DeleteArrQueueRecordAsync(ArrClient client, ArrQueueRecord record, string jobName)
    {
        var status = await client.DeleteQueueRecord(record.Id, ArrConfig.QueueAction.RemoveAndBlocklistAndSearch).ConfigureAwait(false);
        if ((int)status is >= 200 and < 300) return true;

        Log.Warning("[ArrReplacement] Arr instance {Host} returned {Status} when deleting queue record {RecordId} for {JobName}.",
            client.Host, status, record.Id, jobName);
        return false;
    }

    private static async Task<SonarrQueueRecord?> FindSonarrQueueRecordAsync(SonarrClient client, Guid queueItemId, string jobName)
    {
        var queue = await client.GetSonarrQueueAsync().ConfigureAwait(false);
        return queue.Records.FirstOrDefault(x => MatchesQueueRecord(x, queueItemId, jobName));
    }

    private static async Task<RadarrQueueRecord?> FindRadarrQueueRecordAsync(RadarrClient client, Guid queueItemId, string jobName)
    {
        var queue = await client.GetRadarrQueueAsync().ConfigureAwait(false);
        return queue.Records.FirstOrDefault(x => MatchesQueueRecord(x, queueItemId, jobName));
    }

    private static async Task<List<ArrHistoryRecord>> GetMatchingHistoryRecordsAsync(ArrClient client, Guid queueItemId, string jobName)
    {
        var history = await client.GetHistoryAsync(pageSize: 1000).ConfigureAwait(false);
        return history.Records
            .Where(x => MatchesHistoryRecord(x, queueItemId, jobName))
            .ToList();
    }

    private static async Task MarkHistoryRecordsFailedAsync(ArrClient client, IReadOnlyCollection<ArrHistoryRecord> records, string jobName)
    {
        foreach (var historyId in records.Select(x => x.Id).Where(x => x > 0).Distinct())
        {
            if (!await client.MarkHistoryFailedAsync(historyId).ConfigureAwait(false))
            {
                Log.Warning("[ArrReplacement] Arr instance {Host} did not mark history record {HistoryId} failed for {JobName}.",
                    client.Host, historyId, jobName);
            }
        }
    }

    private static bool MatchesQueueRecord(ArrQueueRecord record, Guid queueItemId, string jobName)
    {
        var queueItemIdText = queueItemId.ToString();
        return string.Equals(record.DownloadId, queueItemIdText, StringComparison.OrdinalIgnoreCase)
               || string.Equals(record.Title, jobName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeReleaseName(record.Title), NormalizeReleaseName(jobName), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesHistoryRecord(ArrHistoryRecord record, Guid queueItemId, string jobName)
    {
        var queueItemIdText = queueItemId.ToString();
        return (record.Data?.Values.Any(x => string.Equals(x, queueItemIdText, StringComparison.OrdinalIgnoreCase)) ?? false)
               || string.Equals(record.SourceTitle, jobName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeReleaseName(record.SourceTitle), NormalizeReleaseName(jobName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeReleaseName(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ReleaseSeparatorRegex().Replace(value, string.Empty).ToLowerInvariant();

    private static async Task<List<int>> ResolveSonarrQueueEpisodeIdsAsync(SonarrClient client, SonarrQueueRecord record, IReadOnlyCollection<string> fileNames)
    {
        var explicitEpisodeIds = record.Episodes
            .Select(x => x.Id)
            .Concat(record.Episode != null ? [record.Episode.Id] : [])
            .Concat(record.EpisodeId > 0 ? [record.EpisodeId] : [])
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (fileNames.Count == 0) return explicitEpisodeIds;

        var seriesIds = new[] { record.SeriesId }
            .Concat(record.Episodes.Select(x => x.SeriesId))
            .Concat(record.Episode != null ? [record.Episode.SeriesId] : [])
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        var parsedIds = await ResolveSonarrEpisodeIdsFromSeriesAsync(client, seriesIds, fileNames).ConfigureAwait(false);
        return parsedIds.Count > 0 ? parsedIds : explicitEpisodeIds;
    }

    private static async Task<List<int>> ResolveSonarrEpisodeIdsFromSeriesAsync(SonarrClient client, IReadOnlyCollection<int> seriesIds, IReadOnlyCollection<string> fileNames)
    {
        var requestedEpisodes = fileNames
            .SelectMany(ExtractEpisodeNumbers)
            .Distinct()
            .ToList();
        if (requestedEpisodes.Count == 0 || seriesIds.Count == 0) return [];

        var episodeIds = new HashSet<int>();
        foreach (var seriesId in seriesIds)
        {
            var episodes = await client.GetEpisodes(seriesId).ConfigureAwait(false);
            foreach (var episode in episodes)
            {
                if (requestedEpisodes.Contains(new EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber)))
                    episodeIds.Add(episode.Id);
            }
        }

        return episodeIds.ToList();
    }

    private static IEnumerable<EpisodeKey> ExtractEpisodeNumbers(string fileName)
    {
        var parsed = new HashSet<EpisodeKey>();

        foreach (Match rangeMatch in SonarrEpisodeRangeRegex().Matches(fileName))
        {
            if (!TryParsePositiveInt(rangeMatch.Groups["season"].Value, out var season) ||
                !TryParsePositiveInt(rangeMatch.Groups["start"].Value, out var startEpisode) ||
                !TryParsePositiveInt(rangeMatch.Groups["end"].Value, out var endEpisode)) continue;

            if (endEpisode < startEpisode) (startEpisode, endEpisode) = (endEpisode, startEpisode);
            for (var episode = startEpisode; episode <= endEpisode; episode++)
                parsed.Add(new EpisodeKey(season, episode));
        }

        foreach (Match tokenMatch in SonarrEpisodeTokenRegex().Matches(fileName))
        {
            if (!TryParsePositiveInt(tokenMatch.Groups["season"].Value, out var season)) continue;
            foreach (Match episodeMatch in EpisodePartRegex().Matches(tokenMatch.Groups["episodes"].Value))
            {
                if (TryParsePositiveInt(episodeMatch.Groups["episode"].Value, out var episode))
                    parsed.Add(new EpisodeKey(season, episode));
            }
        }

        foreach (Match match in SonarrEpisodeXRegex().Matches(fileName))
        {
            if (TryParsePositiveInt(match.Groups["season"].Value, out var season) &&
                TryParsePositiveInt(match.Groups["episode"].Value, out var episode))
                parsed.Add(new EpisodeKey(season, episode));
        }

        return parsed;
    }

    private static bool TryParsePositiveInt(string value, out int result) =>
        int.TryParse(value, out result) && result > 0;

    private readonly record struct EpisodeKey(int SeasonNumber, int EpisodeNumber);

    [GeneratedRegex(@"[\W_]+", RegexOptions.Compiled)]
    private static partial Regex ReleaseSeparatorRegex();

    [GeneratedRegex(@"S(?<season>\d{1,2})[ ._\-]*E(?<start>\d{1,3})[ ._\-]*(?:-|to|through|thru)[ ._\-]*E?(?<end>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SonarrEpisodeRangeRegex();

    [GeneratedRegex(@"S(?<season>\d{1,2})(?<episodes>(?:[ ._\-]*E\d{1,3})+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SonarrEpisodeTokenRegex();

    [GeneratedRegex(@"E(?<episode>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EpisodePartRegex();

    [GeneratedRegex(@"(?<!\d)(?<season>\d{1,2})x(?<episode>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SonarrEpisodeXRegex();
}