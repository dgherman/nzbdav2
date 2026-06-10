using System.Text.Json.Serialization;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Clients.RadarrSonarr.SonarrModels;

public class SonarrQueueRecord: ArrQueueRecord
{
    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("episodeId")]
    public int EpisodeId { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episode")]
    public SonarrEpisode? Episode { get; set; }

    [JsonPropertyName("episodes")]
    public List<SonarrEpisode> Episodes { get; set; } = [];
}