using NzbWebDAV.MigrateToInfinidysk.Io;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class ArchivePathGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Check_ArchivePathEqualsSourceDb_IsRejected()
    {
        var sourceDb = Path.Combine(_dir, "source", "db.sqlite");
        var targetDb = Path.Combine(_dir, "target", "db.sqlite");

        var result = ArchivePathGuard.Check(archivePath: sourceDb, sourceDbPath: sourceDb, targetDbPath: targetDb);

        Assert.False(result.IsValid);
        Assert.Contains("source", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_ArchivePathEqualsTargetDb_IsRejected()
    {
        var sourceDb = Path.Combine(_dir, "source", "db.sqlite");
        var targetDb = Path.Combine(_dir, "target", "db.sqlite");

        var result = ArchivePathGuard.Check(archivePath: targetDb, sourceDbPath: sourceDb, targetDbPath: targetDb);

        Assert.False(result.IsValid);
        Assert.Contains("target", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_ArchivePathEquivalentViaDotDotTraversal_IsRejected()
    {
        var sourceDb = Path.Combine(_dir, "source", "db.sqlite");
        var targetDb = Path.Combine(_dir, "target", "db.sqlite");
        var sneakyPath = Path.Combine(_dir, "target", "..", "source", "db.sqlite");

        var result = ArchivePathGuard.Check(archivePath: sneakyPath, sourceDbPath: sourceDb, targetDbPath: targetDb);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Check_ArchivePathThroughSymlinkedAliasOfSourceDir_IsRejected()
    {
        // Round-3 review repro: Path.GetFullPath (round 2's implementation) normalizes `..` and
        // casing but does NOT follow symlinks. A symlinked alias directory pointing at the
        // source config dir let --archive-path through the alias overwrite the real source
        // db.sqlite undetected by the round-2 guard.
        var sourceDir = Path.Combine(_dir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceDb = Path.Combine(sourceDir, "db.sqlite");
        File.WriteAllText(sourceDb, "not json - this is the real nzbdav2 database");
        var targetDb = Path.Combine(_dir, "target", "db.sqlite");

        var aliasDir = Path.Combine(_dir, "alias-to-source");
        if (!TryCreateDirectorySymlink(aliasDir, sourceDir))
            return; // platform/sandbox can't create symlinks without elevation - skip gracefully

        var archivePathThroughAlias = Path.Combine(aliasDir, "db.sqlite");

        var result = ArchivePathGuard.Check(archivePath: archivePathThroughAlias, sourceDbPath: sourceDb, targetDbPath: targetDb);

        Assert.False(result.IsValid);
        Assert.Contains("source", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_ArchivePathThroughSymlinkedAliasOfTargetDir_IsRejected()
    {
        var sourceDb = Path.Combine(_dir, "source", "db.sqlite");
        var targetDir = Path.Combine(_dir, "target");
        Directory.CreateDirectory(targetDir);
        var targetDb = Path.Combine(targetDir, "db.sqlite");
        File.WriteAllText(targetDb, "not json - this is the real infinidysk database");

        var aliasDir = Path.Combine(_dir, "alias-to-target");
        if (!TryCreateDirectorySymlink(aliasDir, targetDir))
            return; // platform/sandbox can't create symlinks without elevation - skip gracefully

        var archivePathThroughAlias = Path.Combine(aliasDir, "db.sqlite");

        var result = ArchivePathGuard.Check(archivePath: archivePathThroughAlias, sourceDbPath: sourceDb, targetDbPath: targetDb);

        Assert.False(result.IsValid);
        Assert.Contains("target", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void Check_ArchivePathElsewhere_IsAccepted()
    {
        var sourceDb = Path.Combine(_dir, "source", "db.sqlite");
        var targetDb = Path.Combine(_dir, "target", "db.sqlite");
        var archivePath = Path.Combine(_dir, "target", "nzbdav2-migration-archive.json");

        var result = ArchivePathGuard.Check(archivePath: archivePath, sourceDbPath: sourceDb, targetDbPath: targetDb);

        Assert.True(result.IsValid);
    }
}
