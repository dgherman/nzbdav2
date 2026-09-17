namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// Converts nzbdav2's sparse SegmentFallbacks (Dictionary&lt;int index, string[] altIds&gt;)
/// into infinidysk's SegmentFallbackIds shape: a dense array aligned by segment index, with
/// an empty array where nzbdav2 recorded no fallback.
/// </summary>
public static class SegmentFallbackMapper
{
    public static string[][]? ToAlignedArray(Dictionary<int, string[]>? source, int segmentCount)
    {
        if (source == null) return null;

        var result = new string[segmentCount][];
        for (var i = 0; i < segmentCount; i++)
            result[i] = source.TryGetValue(i, out var ids) ? ids : [];

        return result;
    }
}
