using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class MigrationApplierTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"applier-fixture-{Guid.NewGuid()}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Apply_ArchiveWriteFailsBeforeDbTouched_LeavesTargetCompletelyUnchanged()
    {
        using var conn = CreateFixture();
        var result = OneAccountResult();

        // A directory path (not a file) as the archive destination's parent guarantees the
        // write fails - simulates "archive write fails" without relying on filesystem
        // permission quirks that vary across CI/sandbox environments.
        var badArchivePath = Path.Combine(_dbPath + "-does-not-exist-dir", "nested", "archive.json");

        Assert.Throws<DirectoryNotFoundException>(() => MigrationApplier.Apply(conn, result, badArchivePath));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Accounts";
        Assert.Equal(0L, cmd.ExecuteScalar());
        Assert.False(File.Exists(badArchivePath));
    }

    [Fact]
    public void Apply_ArchivePathIsExistingDirectory_RejectsBeforeAnyWrite()
    {
        // Exact round-3 review repro: --archive-path pointed at an existing directory. The temp
        // file (a sibling path, not inside that directory) would otherwise write fine and let
        // the DB transaction commit - only the final File.Move would fail, after commit.
        using var conn = CreateFixture();
        var result = OneAccountResult();

        var archiveDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var archivePath = archiveDir; // the directory itself, not a file inside it

            Assert.Throws<IOException>(() => MigrationApplier.Apply(conn, result, archivePath));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Accounts";
            Assert.Equal(0L, cmd.ExecuteScalar());
            // the directory itself must be untouched (not deleted, no stray temp files inside)
            Assert.True(Directory.Exists(archiveDir));
            Assert.Empty(Directory.GetFileSystemEntries(archiveDir));
        }
        finally
        {
            Directory.Delete(archiveDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_Success_WritesDbAndFinalArchiveAtomically()
    {
        using var conn = CreateFixture();
        var result = OneAccountResult();
        var archivePath = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid()}.json");

        try
        {
            MigrationApplier.Apply(conn, result, archivePath);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Accounts";
            Assert.Equal(1L, cmd.ExecuteScalar());
            Assert.True(File.Exists(archivePath));
            // no leftover temp file
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(archivePath)}.tmp-*"));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void Apply_DbWriteFails_LeavesNoTempArchiveBehind()
    {
        using var conn = CreateFixture();
        // Two admin rows violate Accounts' own unique key semantics here by duplicating the
        // primary key (Type, Username), forcing SqliteTargetWriter.Apply to throw mid-transaction.
        var duplicateAccount = new TargetAccount(1, "alice", "hash", "salt");
        var result = new MigrationResult(
            Success: true, Errors: [], Warnings: [], Counts: new Dictionary<string, TableCounts>(),
            DavItems: [], DavNzbFiles: [], DavMultipartFiles: [], QueueItems: [], QueueNzbContents: [],
            HistoryItems: [], ConfigItems: [], Accounts: [duplicateAccount, duplicateAccount],
            HealthCheckResults: [], HealthCheckStats: [],
            Archive: new ArchivePayload([], [], [], [], [], [], [], [], [], [], [], []));
        var archivePath = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid()}.json");

        Assert.ThrowsAny<Exception>(() => MigrationApplier.Apply(conn, result, archivePath));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Accounts";
        Assert.Equal(0L, cmd.ExecuteScalar());
        Assert.False(File.Exists(archivePath));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(archivePath)}.tmp-*"));
    }

    private static MigrationResult OneAccountResult() => new(
        Success: true, Errors: [], Warnings: [], Counts: new Dictionary<string, TableCounts>(),
        DavItems: [], DavNzbFiles: [], DavMultipartFiles: [], QueueItems: [], QueueNzbContents: [],
        HistoryItems: [], ConfigItems: [], Accounts: [new TargetAccount(1, "alice", "hash", "salt")],
        HealthCheckResults: [], HealthCheckStats: [],
        Archive: new ArchivePayload([], [], [], [], [], [], [], [], [], [], [], []));

    private SqliteConnection CreateFixture()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
