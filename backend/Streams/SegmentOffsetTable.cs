namespace NzbWebDAV.Streams;

/// <summary>
/// Builds the cumulative byte-offset table used by NzbFileStream for O(log N) seeking.
/// Succeeds only when sizes are well-formed and sum EXACTLY to the expected file size.
/// Approximate or yEnc-encoded (non-decoded) sizes are rejected — callers trust the offsets
/// as ground truth when discarding bytes during a seek, so a wrong offset would silently corrupt output.
/// </summary>
public static class SegmentOffsetTable
{
    public static bool TryBuild(long[]? segmentSizes, int segmentCount, long expectedFileSize, out long[]? offsets)
    {
        offsets = null;
        if (segmentSizes == null || segmentSizes.Length != segmentCount) return false;

        var result = new long[segmentSizes.Length + 1];
        long current = 0;
        for (int i = 0; i < segmentSizes.Length; i++)
        {
            if (segmentSizes[i] < 0) return false;
            result[i] = current;
            current += segmentSizes[i];
        }
        result[^1] = current;

        if (current != expectedFileSize) return false;
        offsets = result;
        return true;
    }
}
