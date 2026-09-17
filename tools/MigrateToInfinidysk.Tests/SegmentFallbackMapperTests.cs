using NzbWebDAV.MigrateToInfinidysk.Mapping;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class SegmentFallbackMapperTests
{
    [Fact]
    public void ToAlignedArray_NullSource_ReturnsNull()
    {
        var result = SegmentFallbackMapper.ToAlignedArray(null, segmentCount: 3);

        Assert.Null(result);
    }

    [Fact]
    public void ToAlignedArray_SparseDictionary_FillsAbsentIndicesWithEmptyArrays()
    {
        var source = new Dictionary<int, string[]> { [1] = ["fallback-a", "fallback-b"] };

        var result = SegmentFallbackMapper.ToAlignedArray(source, segmentCount: 3);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Length);
        Assert.Empty(result[0]);
        Assert.Equal(["fallback-a", "fallback-b"], result[1]);
        Assert.Empty(result[2]);
    }
}
