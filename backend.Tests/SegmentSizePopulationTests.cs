using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Tests;

public class SegmentSizePopulationTests
{
    private static DavMultipartFile.FilePart Part(long[]? sizes) => new()
    {
        SegmentIds = new[] { "a@x", "b@x" },
        SegmentIdByteRange = LongRange.FromStartAndSize(0, 200),
        FilePartByteRange = LongRange.FromStartAndSize(0, 200),
        SegmentSizes = sizes,
    };

    [Fact]
    public void NeedsPopulation_TrueWhenAnyPartNull()
    {
        var meta = new DavMultipartFile.Meta { FileParts = new[] { Part(new long[] { 100, 100 }), Part(null) } };
        Assert.True(SegmentSizePopulation.NeedsPopulation(meta));
    }

    [Fact]
    public void NeedsPopulation_FalseWhenAllPresent()
    {
        var meta = new DavMultipartFile.Meta { FileParts = new[] { Part(new long[] { 100, 100 }) } };
        Assert.False(SegmentSizePopulation.NeedsPopulation(meta));
    }

    [Fact]
    public void IsValidForPart_TrueWhenSumsToPartSize()
        => Assert.True(SegmentSizePopulation.IsValidForPart(Part(null), new long[] { 100, 100 }));

    [Fact]
    public void IsValidForPart_FalseWhenSumWrong_OrCountWrong()
    {
        Assert.False(SegmentSizePopulation.IsValidForPart(Part(null), new long[] { 103, 103 }));
        Assert.False(SegmentSizePopulation.IsValidForPart(Part(null), new long[] { 200 }));
    }
}
