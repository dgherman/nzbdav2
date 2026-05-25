using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.WebDav;

/// <summary>
/// Pure decide/validate logic for lazily populating DavMultipartFile.FilePart.SegmentSizes.
/// Network fetching lives in DatabaseStoreMultipartFile; this stays unit-testable.
/// </summary>
public static class SegmentSizePopulation
{
    public static bool NeedsPopulation(DavMultipartFile.Meta meta) =>
        meta.FileParts.Any(p => p.SegmentSizes == null || p.SegmentSizes.Length != p.SegmentIds.Length);

    /// <summary>True only if the computed sizes are well-formed and sum exactly to the part's decoded size.</summary>
    public static bool IsValidForPart(DavMultipartFile.FilePart part, long[] computedSizes) =>
        SegmentOffsetTable.TryBuild(computedSizes, part.SegmentIds.Length, part.SegmentIdByteRange.Count, out _);
}
