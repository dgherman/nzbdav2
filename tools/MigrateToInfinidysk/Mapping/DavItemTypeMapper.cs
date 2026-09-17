namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// Translates nzbdav2's legacy DavItem.Type enum into infinidysk's Type+SubType pair,
/// per infinidysk migration 20260129182923_Update-DavItems-Type-And-SubType.cs.
/// </summary>
public static class DavItemTypeMapper
{
    // nzbdav2 legacy DavItem.ItemType values (backend/Database/Models/DavItem.cs)
    public enum LegacyType
    {
        Directory = 1,
        SymlinkRoot = 2,
        NzbFile = 3,
        RarFile = 4,
        IdsRoot = 5,
        MultipartFile = 6,
    }

    public readonly record struct TargetType(int Type, int SubType);

    // Fixed root DavItem ids in nzbdav2, identified by their last path segment (0000/0001/0002).
    private static readonly Guid RootId = Guid.Parse("00000000-0000-0000-0000-000000000000");
    private static readonly Guid NzbFolderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ContentFolderId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public static TargetType Map(Guid davItemId, LegacyType legacyType)
    {
        if (davItemId == RootId) return new TargetType(1, 102);
        if (davItemId == NzbFolderId) return new TargetType(1, 103);
        if (davItemId == ContentFolderId) return new TargetType(1, 104);

        return legacyType switch
        {
            LegacyType.Directory => new TargetType(1, 101),
            LegacyType.SymlinkRoot => new TargetType(1, 105),
            LegacyType.NzbFile => new TargetType(2, 201),
            // SubType 203 (MultipartFile), not 202 (RAR reader): this tool always converts
            // legacy DavRarFile rows into DavMultipartFiles target rows (see
            // MultipartFileMapper.FromRarFile) - it never writes infinidysk's DavRarFiles table
            // at all. 202 would point a DavItems row at a table nothing was ever written to.
            LegacyType.RarFile => new TargetType(2, 203),
            LegacyType.IdsRoot => new TargetType(1, 106),
            LegacyType.MultipartFile => new TargetType(2, 203),
            _ => throw new ArgumentOutOfRangeException(nameof(legacyType), legacyType, "Unknown legacy DavItem type"),
        };
    }
}
