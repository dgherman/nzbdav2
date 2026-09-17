using System.Text;
using ZstdSharp;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Decodes nzbdav2's TEXT blob columns (SegmentIds, SegmentFallbacks, Metadata, RarParts,
/// NzbContents), which are stored as either Zstd-compressed-then-base64'd-with-a-"ZSTD:"-prefix,
/// or (legacy) plain JSON text. Mirrors nzbdav2's own backend/Utils/CompressionUtil.cs
/// (read/write source, not referenced directly since this tool lives outside the main project).
/// </summary>
public static class SourceBlobCodec
{
    private const string ZstdPrefix = "ZSTD:";

    public static bool IsCompressed(string storedValue) => storedValue.StartsWith(ZstdPrefix, StringComparison.Ordinal);

    public static string Decode(string storedValue)
    {
        if (!IsCompressed(storedValue))
            return storedValue;

        var base64 = storedValue.AsSpan(ZstdPrefix.Length);
        var compressed = Convert.FromBase64String(base64.ToString());
        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(compressed);
        return Encoding.UTF8.GetString(decompressed);
    }
}
