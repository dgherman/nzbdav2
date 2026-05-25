using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests;

public class SegmentOffsetTableTests
{
    [Fact]
    public void DecodedSizes_SummingToFileSize_BuildOffsets()
    {
        var ok = SegmentOffsetTable.TryBuild(new long[] { 100, 100, 50 }, 3, 250, out var offsets);
        Assert.True(ok);
        Assert.Equal(new long[] { 0, 100, 200, 250 }, offsets);
    }

    [Fact]
    public void EncodedSizes_OverShooting_AreRejected()
    {
        var ok = SegmentOffsetTable.TryBuild(new long[] { 103, 103, 52 }, 3, 250, out var offsets);
        Assert.False(ok);
        Assert.Null(offsets);
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.False(SegmentOffsetTable.TryBuild(null, 3, 250, out var offsets));
        Assert.Null(offsets);
    }

    [Fact]
    public void LengthMismatch_IsRejected()
    {
        Assert.False(SegmentOffsetTable.TryBuild(new long[] { 100, 150 }, 3, 250, out var offsets));
        Assert.Null(offsets);
    }

    [Fact]
    public void NegativeSize_IsRejected()
    {
        Assert.False(SegmentOffsetTable.TryBuild(new long[] { 300, -50, 0 }, 3, 250, out var offsets));
        Assert.Null(offsets);
    }
}
