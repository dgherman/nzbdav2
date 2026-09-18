namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// infinidysk's DavMultipartFile.Meta has no ObfuscationKey field, so RAR-obfuscated
/// content cannot be faithfully migrated. Flags rows that must be skipped rather than
/// silently imported with broken playback.
/// </summary>
public static class ObfuscationDetector
{
    /// <summary>
    /// A row is unmigratable when it has an explicit obfuscation key (definite obfuscation),
    /// or when it came from a RAR archive with no recorded key: nzbdav2's RarDeobfuscationStream
    /// falls back to content-sniffed default-key auto-detection in that case (see
    /// backend/Streams/RarDeobfuscationStream.cs), which is content-based and cannot be
    /// reproduced from DB rows alone.
    /// </summary>
    public static bool IsUnmigratable(byte[]? obfuscationKey, bool wasRarSourced)
    {
        if (obfuscationKey != null) return true;
        return wasRarSourced;
    }
}
