namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

public class GetHealthCheckQueueResponse : BaseApiResponse
{
    public List<HealthCheckQueueItem> Items { get; init; } = [];
    public int UncheckedCount { get; init; }
    public int PendingCount { get; init; }

    public class HealthCheckQueueItem
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
        public string? JobName { get; init; }
        public required DateTimeOffset? ReleaseDate { get; init; }
        public required DateTimeOffset? LastHealthCheck { get; init; }
        public required DateTimeOffset? NextHealthCheck { get; init; }
        public required string OperationType { get; init; } // "STAT" or "HEAD"
        public required int Progress { get; init; } // Active health check progress (0-100), updated via WebSocket
        public string? LatestResult { get; init; } // Latest health check result: "Healthy", "Unhealthy", etc.
    }
}