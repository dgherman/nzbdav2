using System.Text;
using NzbWebDAV.MigrateToInfinidysk.Io;
using ZstdSharp;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class SourceBlobCodecTests
{
    [Fact]
    public void Decode_PlainLegacyJson_ReturnsAsIs()
    {
        var result = SourceBlobCodec.Decode("[\"seg-1\",\"seg-2\"]");

        Assert.Equal("[\"seg-1\",\"seg-2\"]", result);
    }

    [Fact]
    public void Decode_ZstdCompressedValue_DecompressesToOriginalText()
    {
        const string original = "[\"seg-1\",\"seg-2\",\"seg-3\"]";
        using var compressor = new Compressor(1);
        var compressed = compressor.Wrap(Encoding.UTF8.GetBytes(original));
        var stored = "ZSTD:" + Convert.ToBase64String(compressed);

        var result = SourceBlobCodec.Decode(stored);

        Assert.Equal(original, result);
    }
}
