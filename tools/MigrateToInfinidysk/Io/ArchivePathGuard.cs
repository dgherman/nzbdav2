namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Refuses to write the sidecar JSON archive over the source or target database. Reproduced
/// bug: an unrestricted --archive-path pointed at the source db.sqlite silently overwrote it
/// via File.WriteAllText. Canonicalizes all three paths before comparing so relative
/// segments (`..`) or differing-but-equivalent path spellings can't slip past a naive
/// string-equality check.
/// </summary>
public static class ArchivePathGuard
{
    public readonly record struct Result(bool IsValid, string? ErrorMessage)
    {
        public static Result Valid() => new(true, null);
        public static Result Invalid(string message) => new(false, message);
    }

    public static Result Check(string archivePath, string sourceDbPath, string targetDbPath)
    {
        var canonicalArchive = Canonicalize(archivePath);
        var canonicalSource = Canonicalize(sourceDbPath);
        var canonicalTarget = Canonicalize(targetDbPath);

        if (PathsEqual(canonicalArchive, canonicalSource))
        {
            return Result.Invalid(
                $"--archive-path resolves to the same file as the source database ({canonicalSource}). " +
                "Refusing to write - this tool must never modify the nzbdav2 source database. Choose a " +
                "different --archive-path.");
        }

        if (PathsEqual(canonicalArchive, canonicalTarget))
        {
            return Result.Invalid(
                $"--archive-path resolves to the same file as the target database ({canonicalTarget}). " +
                "Refusing to write - this would destroy the infinidysk database you just migrated into. " +
                "Choose a different --archive-path.");
        }

        return Result.Valid();
    }

    private static string Canonicalize(string path) => Path.GetFullPath(path);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}
