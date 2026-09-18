using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class MultipartFileMapperTests
{
    [Fact]
    public void Map_OrdinaryRow_ProducesEquivalentMetaWithNoObfuscationKey()
    {
        var id = Guid.NewGuid();
        var source = new SourceDavMultipartFile(
            Id: id,
            AesParams: new SourceAesParams(1000, [1, 2, 3], [4, 5, 6]),
            ObfuscationKey: null,
            FileParts:
            [
                new SourceSegmentFilePart(
                    SegmentIds: ["seg-1", "seg-2"],
                    SegmentIdByteRangeStart: 0,
                    SegmentIdByteRangeEnd: 500,
                    FilePartByteRangeStart: 0,
                    FilePartByteRangeEnd: 500,
                    SegmentFallbacks: null)
            ]);

        var result = MultipartFileMapper.Map(source, wasRarSourced: false);

        Assert.False(result.IsUnmigratable);
        Assert.NotNull(result.Meta);
        Assert.Equal(1000, result.Meta!.AesParams!.DecodedSize);
        Assert.Single(result.Meta.FileParts);
        Assert.Equal(["seg-1", "seg-2"], result.Meta.FileParts[0].SegmentIds);
        Assert.Equal(0, result.Meta.FileParts[0].SegmentIdByteRange.StartInclusive);
        Assert.Equal(500, result.Meta.FileParts[0].SegmentIdByteRange.EndExclusive);
        // Trust provenance can't be reconstructed from a DB-only import - always left unset,
        // which forces infinidysk to fall back to safe header-probed seeking.
        Assert.Null(result.Meta.FileParts[0].SegmentByteRangesTrusted);
        Assert.Null(result.Meta.FileParts[0].SegmentByteRanges);
    }

    [Fact]
    public void Map_RowWithExplicitObfuscationKey_IsFlaggedUnmigratable()
    {
        var source = new SourceDavMultipartFile(
            Id: Guid.NewGuid(),
            AesParams: null,
            ObfuscationKey: [0xB0, 0x41, 0xC2, 0xCE],
            FileParts: [new SourceSegmentFilePart(["seg"], 0, 100, 0, 100, null)]);

        var result = MultipartFileMapper.Map(source, wasRarSourced: false);

        Assert.True(result.IsUnmigratable);
        Assert.Null(result.Meta);
        Assert.Contains("ObfuscationKey", result.SkipReason);
    }

    [Fact]
    public void Map_RarSourcedRowWithNullKey_IsFlaggedUnmigratable()
    {
        var source = new SourceDavMultipartFile(
            Id: Guid.NewGuid(),
            AesParams: null,
            ObfuscationKey: null,
            FileParts: [new SourceSegmentFilePart(["seg"], 0, 100, 0, 100, null)]);

        var result = MultipartFileMapper.Map(source, wasRarSourced: true);

        Assert.True(result.IsUnmigratable);
        Assert.Null(result.Meta);
    }

    [Fact]
    public void Map_SegmentFallbacks_AreAlignedBySegmentIndex()
    {
        var source = new SourceDavMultipartFile(
            Id: Guid.NewGuid(),
            AesParams: null,
            ObfuscationKey: null,
            FileParts:
            [
                new SourceSegmentFilePart(
                    SegmentIds: ["seg-1", "seg-2"],
                    SegmentIdByteRangeStart: 0,
                    SegmentIdByteRangeEnd: 200,
                    FilePartByteRangeStart: 0,
                    FilePartByteRangeEnd: 200,
                    SegmentFallbacks: new Dictionary<int, string[]> { [1] = ["fallback-seg-2"] })
            ]);

        var result = MultipartFileMapper.Map(source, wasRarSourced: false);

        Assert.False(result.IsUnmigratable);
        var fallbackIds = result.Meta!.FileParts[0].SegmentFallbackIds;
        Assert.NotNull(fallbackIds);
        Assert.Empty(fallbackIds![0]);
        Assert.Equal(["fallback-seg-2"], fallbackIds[1]);
    }
}
