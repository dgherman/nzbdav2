namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Refuses to write the sidecar JSON archive over the source or target database. Reproduced
/// bug: an unrestricted --archive-path pointed at the source db.sqlite silently overwrote it
/// via File.WriteAllText. Resolves all three paths to their real, symlink-followed location
/// before comparing, so neither a relative segment (`..`), a differing-but-equivalent path
/// spelling, nor a symlinked/aliased directory pointing at the same underlying file can slip
/// past the check. Path.GetFullPath alone (round 2's implementation) normalizes `..` and
/// casing but does NOT follow symlinks - reproduced bug: a symlinked alias directory pointed
/// at the source config dir let --archive-path through the alias overwrite the real source
/// db.sqlite undetected.
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
        var canonicalArchive = ResolveRealPath(archivePath);
        var canonicalSource = ResolveRealPath(sourceDbPath);
        var canonicalTarget = ResolveRealPath(targetDbPath);

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

    /// <summary>
    /// Resolves a path to its real, symlink-followed location - a realpath(3)/GetFinalPathName
    /// equivalent. .NET has no single built-in for this (FileSystemInfo.ResolveLinkTarget only
    /// resolves the final path component if it is itself a symlink; it does not walk ancestor
    /// directories that may themselves be symlinks/junctions). So each ancestor directory is
    /// resolved first, recursively, before the final component is combined and checked - this
    /// correctly follows a symlinked/aliased PARENT directory, not just a symlinked leaf file.
    /// Path components that don't exist on disk (e.g. an archive file not yet created) are left
    /// as-is, matching Path.GetFullPath's behavior for the non-symlink case.
    /// </summary>
    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);

        // Reached the filesystem root (GetDirectoryName returns null, or - defensively, in case
        // a platform ever returns the root itself - stops an infinite recursion).
        if (string.IsNullOrEmpty(parent) || string.Equals(parent, full, StringComparison.Ordinal))
            return full;

        var resolvedParent = ResolveRealPath(parent);
        var candidate = Path.Combine(resolvedParent, Path.GetFileName(full));

        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            var target = File.Exists(candidate)
                ? new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)
                : new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
            if (target != null)
                // The symlink's stored target text (e.g. on macOS, /var/... rather than the
                // fully-resolved /private/var/...) may itself pass through further ancestor
                // symlinks, so resolve it again rather than trusting it as final.
                return ResolveRealPath(target.FullName);
        }

        return candidate;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}
