using NzbWebDAV.MigrateToInfinidysk.Mapping;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class DavItemTypeMapperTests
{
    [Theory]
    [InlineData(DavItemTypeMapper.LegacyType.Directory, 1, 101)]
    [InlineData(DavItemTypeMapper.LegacyType.SymlinkRoot, 1, 105)]
    [InlineData(DavItemTypeMapper.LegacyType.NzbFile, 2, 201)]
    // 203 (MultipartFile), not 202 (RAR reader): every legacy DavRarFile row is converted into
    // a DavMultipartFiles target row (see MultipartFileMapper.FromRarFile) - infinidysk's
    // DavRarFiles table is never written to by this tool.
    [InlineData(DavItemTypeMapper.LegacyType.RarFile, 2, 203)]
    [InlineData(DavItemTypeMapper.LegacyType.IdsRoot, 1, 106)]
    [InlineData(DavItemTypeMapper.LegacyType.MultipartFile, 2, 203)]
    public void Map_OrdinaryRows_UsesEnumMapping(DavItemTypeMapper.LegacyType legacyType, int expectedType, int expectedSubType)
    {
        var id = Guid.NewGuid();

        var result = DavItemTypeMapper.Map(id, legacyType);

        Assert.Equal(expectedType, result.Type);
        Assert.Equal(expectedSubType, result.SubType);
    }

    [Fact]
    public void Map_RootId_UsesFixedSubType102RegardlessOfLegacyType()
    {
        var rootId = Guid.Parse("00000000-0000-0000-0000-000000000000");

        var result = DavItemTypeMapper.Map(rootId, DavItemTypeMapper.LegacyType.Directory);

        Assert.Equal(1, result.Type);
        Assert.Equal(102, result.SubType);
    }

    [Fact]
    public void Map_NzbFolderId_UsesFixedSubType103()
    {
        var nzbFolderId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var result = DavItemTypeMapper.Map(nzbFolderId, DavItemTypeMapper.LegacyType.Directory);

        Assert.Equal(1, result.Type);
        Assert.Equal(103, result.SubType);
    }

    [Fact]
    public void Map_ContentFolderId_UsesFixedSubType104()
    {
        var contentFolderId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var result = DavItemTypeMapper.Map(contentFolderId, DavItemTypeMapper.LegacyType.Directory);

        Assert.Equal(1, result.Type);
        Assert.Equal(104, result.SubType);
    }
}
