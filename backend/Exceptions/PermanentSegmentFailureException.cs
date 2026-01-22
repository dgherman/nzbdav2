namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when a segment permanently fails to download (after all retries exhausted)
/// and graceful degradation is disabled. Used by benchmarks to detect and stop early.
/// </summary>
public class PermanentSegmentFailureException(int segmentIndex, string segmentId, string reason)
    : NonRetryableDownloadException($"Segment {segmentIndex} ({segmentId}) permanently failed: {reason}")
{
    public int SegmentIndex => segmentIndex;
    public string SegmentId => segmentId;
    public string FailureReason => reason;
}
