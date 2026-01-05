namespace NzbWebDAV.Database.Models;

/// <summary>
/// Tracks provider performance statistics per NZB for intelligent provider selection.
/// Stores success rate, speed, and failure data to optimize future downloads from the same NZB.
/// </summary>
public class NzbProviderStats
{
    /// <summary>
    /// Composite key: JobName + ProviderIndex
    /// Using JobName instead of QueueItemId because items persist after queue completion
    /// </summary>
    public string JobName { get; set; } = null!;

    public int ProviderIndex { get; set; }

    /// <summary>
    /// Number of segments successfully downloaded from this provider for this NZB
    /// </summary>
    public int SuccessfulSegments { get; set; }

    /// <summary>
    /// Number of segments that failed (timeouts, missing articles, etc.)
    /// </summary>
    public int FailedSegments { get; set; }

    /// <summary>
    /// Total bytes downloaded from this provider for this NZB
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Total time spent downloading in milliseconds
    /// </summary>
    public long TotalTimeMs { get; set; }

    /// <summary>
    /// Last time this provider was used for this NZB
    /// </summary>
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>
    /// Recent weighted average speed in bytes per second (with outlier rejection)
    /// Uses exponential weighted moving average to favor recent performance
    /// </summary>
    public long RecentAverageSpeedBps { get; set; }

    /// <summary>
    /// Calculated all-time average speed in bytes per second
    /// </summary>
    public long AverageSpeedBps => TotalTimeMs > 0 ? (TotalBytes * 1000) / TotalTimeMs : 0;

    /// <summary>
    /// Success rate percentage (0-100)
    /// </summary>
    public double SuccessRate => (SuccessfulSegments + FailedSegments) > 0
        ? (SuccessfulSegments * 100.0) / (SuccessfulSegments + FailedSegments)
        : 0;
}
