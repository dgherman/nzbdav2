using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetFileDetails;

public class GetFileDetailsResponse
{
    public string DavItemId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string? JobName { get; set; }
    public string DownloadUrl { get; set; } = null!;
    public string? NzbDownloadUrl { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastHealthCheck { get; set; }
    public DateTimeOffset? NextHealthCheck { get; set; }
    public int MissingArticleCount { get; set; }
    public int TotalSegments { get; set; }
    public long? MinSegmentSize { get; set; }
    public long? MaxSegmentSize { get; set; }
    public long? AvgSegmentSize { get; set; }
    public List<ProviderStatistic> ProviderStats { get; set; } = new();
    public HealthCheckInfo? LatestHealthCheckResult { get; set; }

    public class ProviderStatistic
    {
        public int ProviderIndex { get; set; }
        public string ProviderHost { get; set; } = null!;
        public int SuccessfulSegments { get; set; }
        public int FailedSegments { get; set; }
        public long TotalBytes { get; set; }
        public long TotalTimeMs { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public long AverageSpeedBps { get; set; }
        public double SuccessRate { get; set; }
    }

    public class HealthCheckInfo
    {
        public HealthCheckResult.HealthResult Result { get; set; }
        public HealthCheckResult.RepairAction RepairStatus { get; set; }
        public string? Message { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
