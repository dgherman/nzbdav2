using System.Text.Json;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests;

public class DavMultipartFileSerializationTests
{
    [Fact]
    public void OldJson_WithoutSegmentSizes_DeserializesWithNull()
    {
        var oldJson = """
        { "AesParams": null, "ObfuscationKey": null, "FileParts": [
          { "SegmentIds": ["a@x","b@x"],
            "SegmentIdByteRange": { "StartInclusive": 0, "EndExclusive": 200 },
            "FilePartByteRange": { "StartInclusive": 10, "EndExclusive": 190 },
            "SegmentFallbacks": null } ] }
        """;
        var meta = JsonSerializer.Deserialize<DavMultipartFile.Meta>(oldJson);
        Assert.NotNull(meta);
        Assert.Null(meta!.FileParts[0].SegmentSizes);
    }

    [Fact]
    public void NewField_RoundTrips()
    {
        var meta = new DavMultipartFile.Meta
        {
            FileParts = new[]
            {
                new DavMultipartFile.FilePart
                {
                    SegmentIds = new[] { "a@x", "b@x" },
                    SegmentIdByteRange = LongRange.FromStartAndSize(0, 200),
                    FilePartByteRange = LongRange.FromStartAndSize(10, 180),
                    SegmentSizes = new long[] { 100, 100 },
                }
            }
        };
        var back = JsonSerializer.Deserialize<DavMultipartFile.Meta>(JsonSerializer.Serialize(meta));
        Assert.Equal(new long[] { 100, 100 }, back!.FileParts[0].SegmentSizes);
    }
}
