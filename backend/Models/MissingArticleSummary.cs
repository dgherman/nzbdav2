namespace NzbWebDAV.Models;

public class MissingArticleItem
{
    public string JobName { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public DateTimeOffset LatestTimestamp { get; set; }
    public int TotalEvents { get; set; }
    public Dictionary<int, int> ProviderCounts { get; set; } = new();
    public bool HasBlockingMissingArticles { get; set; }
    public bool IsImported { get; set; }
}

public class MissingArticleJobSummary
{
    public string JobName { get; set; } = string.Empty;
    public DateTimeOffset LatestTimestamp { get; set; }
    public int TotalEvents { get; set; }
    public Dictionary<int, int> ProviderCounts { get; set; } = new();
    public List<MissingArticleFileSummary> Files { get; set; } = new();
}

public class MissingArticleFileSummary
{
    public string Filename { get; set; } = string.Empty;
    public DateTimeOffset LatestTimestamp { get; set; }
    public int TotalEvents { get; set; }
    public Dictionary<int, int> ProviderCounts { get; set; } = new();
}