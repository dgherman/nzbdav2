using System;
using System.Text;

namespace SharpCompress.Common;

/// <summary>
/// Specifies the type of encoding to use.
/// </summary>
public enum EncodingType
{
    /// <summary>
    /// Uses the default encoding.
    /// </summary>
    Default,

    /// <summary>
    /// Uses UTF-8 encoding.
    /// </summary>
    UTF8,
}

/// <summary>
/// Provides extension methods for archive encoding.
/// </summary>
public static class ArchiveEncodingExtensions
{
#if !NETFRAMEWORK
    /// <summary>
    /// Registers the code pages encoding provider.
    /// </summary>
    static ArchiveEncodingExtensions() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif

    /// <summary>
    /// Gets the encoding based on the archive encoding settings.
    /// </summary>
    /// <param name="encoding">The archive encoding.</param>
    /// <param name="useUtf8">Whether to use UTF-8.</param>
    /// <returns>The encoding.</returns>
    public static Encoding GetEncoding(this IArchiveEncoding encoding, bool useUtf8 = false) =>
        encoding.Forced ?? (useUtf8 ? encoding.UTF8 : encoding.Default);

    /// <summary>
    /// Gets the decoder function for the archive encoding.
    /// </summary>
    /// <param name="encoding">The archive encoding.</param>
    /// <returns>The decoder function.</returns>
    public static Func<byte[], int, int, EncodingType, string> GetDecoder(this IArchiveEncoding encoding) =>
        encoding.CustomDecoder
        ?? (
            (bytes, index, count, type) =>
                encoding.GetEncoding(type == EncodingType.UTF8).GetString(bytes, index, count)
        );

    /// <summary>
    /// Encodes a string using the default encoding.
    /// </summary>
    /// <param name="encoding">The archive encoding.</param>
    /// <param name="str">The string to encode.</param>
    /// <returns>The encoded bytes.</returns>
    public static byte[] Encode(this IArchiveEncoding encoding, string str) => encoding.Default.GetBytes(str);

    /// <summary>
    /// Decodes bytes using the specified encoding type.
    /// </summary>
    /// <param name="encoding">The archive encoding.</param>
    /// <param name="bytes">The bytes to decode.</param>
    /// <param name="type">The encoding type.</param>
    /// <returns>The decoded string.</returns>
    public static string Decode(this IArchiveEncoding encoding, byte[] bytes, EncodingType type = EncodingType.Default) =>
        encoding.Decode(bytes, 0, bytes.Length, type);

    /// <summary>
    /// Decodes a portion of bytes using the specified encoding type.
    /// </summary>
    /// <param name="encoding">The archive encoding.</param>
    /// <param name="bytes">The bytes to decode.</param>
    /// <param name="start">The start index.</param>
    /// <param name="length">The length.</param>
    /// <param name="type">The encoding type.</param>
    /// <returns>The decoded string.</returns>
    public static string Decode(
        this IArchiveEncoding encoding,
        byte[] bytes,
        int start,
        int length,
        EncodingType type = EncodingType.Default
    ) => encoding.GetDecoder()(bytes, start, length, type);
}