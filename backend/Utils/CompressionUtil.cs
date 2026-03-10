using ZstdSharp;

namespace NzbWebDAV.Utils;

/// <summary>
/// Zstandard compression utilities for database payload columns.
/// Compressed data is stored as base64 with a "ZSTD:" prefix so the value
/// converter can auto-detect legacy plain-text JSON on read.
/// </summary>
public static class CompressionUtil
{
    private const string ZstdPrefix = "ZSTD:";
    private const int CompressionLevel = 1; // fast compression, still good ratio

    /// <summary>
    /// Compress a string (typically JSON) using Zstandard and return as prefixed base64.
    /// </summary>
    public static string Compress(string plainText)
    {
        var inputBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        using var compressor = new Compressor(CompressionLevel);
        var compressed = compressor.Wrap(inputBytes);
        return ZstdPrefix + Convert.ToBase64String(compressed);
    }

    /// <summary>
    /// Decompress a value that may be either Zstd-compressed (prefixed) or plain text.
    /// Returns the original string in both cases.
    /// </summary>
    public static string Decompress(string storedValue)
    {
        if (!IsCompressed(storedValue))
            return storedValue;

        var base64 = storedValue.AsSpan(ZstdPrefix.Length);
        var compressed = Convert.FromBase64String(base64.ToString());
        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(compressed);
        return System.Text.Encoding.UTF8.GetString(decompressed);
    }

    /// <summary>
    /// Check whether a stored value is Zstd-compressed.
    /// </summary>
    public static bool IsCompressed(string storedValue)
    {
        return storedValue.StartsWith(ZstdPrefix, StringComparison.Ordinal);
    }
}
