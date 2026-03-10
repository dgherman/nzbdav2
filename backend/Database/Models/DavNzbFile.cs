namespace NzbWebDAV.Database.Models;

public class DavNzbFile
{
    public Guid Id { get; set; } // foreign key to DavItem.Id
    public string[] SegmentIds { get; set; } = [];
    public byte[]? SegmentSizes { get; set; }

    /// <summary>
    /// Fallback message-IDs for segments with duplicate segment numbers in the NZB.
    /// Key = index in SegmentIds array, Value = array of alternative message-IDs.
    /// Null when no duplicates exist (the common case).
    /// </summary>
    public Dictionary<int, string[]>? SegmentFallbacks { get; set; }

    public long[]? GetSegmentSizes()
    {
        if (SegmentSizes == null) return null;
        var result = new long[SegmentSizes.Length / sizeof(long)];
        Buffer.BlockCopy(SegmentSizes, 0, result, 0, SegmentSizes.Length);
        return result;
    }

    public void SetSegmentSizes(long[] sizes)
    {
        var result = new byte[sizes.Length * sizeof(long)];
        Buffer.BlockCopy(sizes, 0, result, 0, result.Length);
        SegmentSizes = result;
    }

    // navigation helpers
    public DavItem? DavItem { get; set; }
}