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
    public void Check_ArchivePathElsewhere_IsAccepted()
    {
        var sourceDb = Path.Combine(_dir, "source", "db.sqlite");
        var targetDb = Path.Combine(_dir, "target", "db.sqlite");
        var archivePath = Path.Combine(_dir, "target", "nzbdav2-migration-archive.json");

        var result = ArchivePathGuard.Check(archivePath: archivePath, sourceDbPath: sourceDb, targetDbPath: targetDb);

        Assert.True(result.IsValid);
    }
}
