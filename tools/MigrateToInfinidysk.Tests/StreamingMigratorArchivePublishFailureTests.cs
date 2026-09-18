using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Round-13 regression: StreamingMigrator.Run publishes the archive (renames the temp file onto
/// the final --archive-path) BEFORE calling target.Commit(), so that the archive is durable
/// before the DB's own point of no return (same ordering MigrationApplier uses). But the old
/// version of this method only cleaned up the TEMP archive path in its finally block - if
/// Commit() itself threw after the archive was already published, the DB transaction rolled back
/// correctly, but the published archive file was left behind: an orphaned artifact claiming
/// migration data exists in the target DB when it actually doesn't (rolled back to nothing).
/// </summary>
public class StreamingMigratorArchivePublishFailureTests : IDisposable
{
    private readonly string _sourceDbPath = Path.Combine(Path.GetTempPath(), $"nzbdav2-commitfail-{Guid.NewGuid()}.sqlite");
    private readonly string _targetDbPath = Path.Combine(Path.GetTempPath(), $"infinidysk-commitfail-{Guid.NewGuid()}.sqlite");
    private readonly string _archivePath = Path.Combine(Path.GetTempPath(), $"archive-commitfail-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_sourceDbPath);
        File.Delete(_targetDbPath);
        if (File.Exists(_archivePath)) File.Delete(_archivePath);
    }

    [Fact]
    public void Run_CommitThrowsAfterArchivePublished_DeletesPublishedArchive_AndIsSafelyRetryable()
    {
        BuildSourceFixture();
        using var target = BuildTargetFixture();
        using var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();

        // Fault-inject: throw immediately before target.Commit() is called - by then the archive
        // has already been finalized and renamed onto _archivePath (StreamingMigrator.Run always
        // publishes before committing), so this reproduces exactly the ordering the bug report
        // named: publish succeeds, then something between publish and commit throws.
        var injected = new InvalidOperationException("injected commit failure");
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            StreamingMigrator.Run(sourceConn, target, _archivePath, new MigrationOptions(null),
                onBeforeCommit: () => throw injected));
        Assert.Same(injected, thrown);

        // The DB transaction rolled back (SqliteTargetWriter.TargetWriteSession's Dispose-without-
        // Commit semantics) - nothing was actually written.
        using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Accounts";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }

        // The published archive must not survive a failed/rolled-back migration - this is the
        // round-13 fix itself.
        Assert.False(File.Exists(_archivePath),
            "archive file was left behind after target.Commit() threw - it should have been deleted " +
            "since the DB transaction it was supposed to accompany rolled back");

        // Retryable: a fresh --apply-equivalent call against the same (still-open) source/target
        // connections and the same archive path must succeed cleanly, not fail because of any
        // state left over from the aborted run above.
        var retryResult = StreamingMigrator.Run(sourceConn, target, _archivePath, new MigrationOptions(null));

        Assert.True(retryResult.Success, string.Join("; ", retryResult.Errors));
        Assert.True(File.Exists(_archivePath));
        using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = "SELECT Username FROM Accounts";
            Assert.Equal("admin", cmd.ExecuteScalar());
        }
    }

    private void BuildSourceFixture()
    {
        using var conn = new SqliteConnection($"Data Source={_sourceDbPath}");
        conn.Open();
        Execute(conn, """
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO __EFMigrationsHistory VALUES ('20251113081523_Populate-Usenet-Providers-Config', '10.0.4');

            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, Path TEXT NOT NULL,
                ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER, MediaInfo TEXT,
                IsCorrupted INTEGER NOT NULL DEFAULT 0, CorruptionReason TEXT, HistoryItemId TEXT);

            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL, SegmentFallbacks TEXT);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE DavRarFiles (Id TEXT PRIMARY KEY, RarParts TEXT NOT NULL);

            CREATE TABLE QueueItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, FileName TEXT NOT NULL, JobName TEXT NOT NULL,
                NzbFileSize INTEGER NOT NULL, TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL,
                Priority INTEGER NOT NULL, PostProcessing INTEGER NOT NULL, PauseUntil TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);

            CREATE TABLE HistoryItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, CompletedAt TEXT NOT NULL, FileName TEXT NOT NULL,
                JobName TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT,
                DownloadDirId TEXT, IsHidden INTEGER NOT NULL DEFAULT 0, HiddenAt TEXT, NzbContents TEXT,
                FailureReason TEXT, IsImported INTEGER NOT NULL DEFAULT 0, IsArchived INTEGER NOT NULL DEFAULT 0,
                ArchivedAt TEXT);

            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            INSERT INTO Accounts VALUES (1, 'admin', 'hash', 'salt');
            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT, Operation TEXT NOT NULL DEFAULT 'UNKNOWN');
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY (DateStartInclusive, DateEndExclusive, Result, RepairStatus));
            """);
    }

    private SqliteConnection BuildTargetFixture()
    {
        var conn = new SqliteConnection($"Data Source={_targetDbPath};Mode=ReadWriteCreate");
        conn.Open();
        Execute(conn, """
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL DEFAULT 0,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL DEFAULT 0, FileBlobId TEXT, HistoryItemId TEXT, NzbBlobId TEXT,
                ArrDownloadId TEXT, GeneratedStrmOutputRoot TEXT, GeneratedStrmPath TEXT, GeneratedStrmTarget TEXT,
                GeneratedSymlinkOutputRoot TEXT, GeneratedSymlinkPath TEXT, GeneratedSymlinkTarget TEXT);

            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE DavRarFiles (Id TEXT PRIMARY KEY, RarParts TEXT NOT NULL);

            CREATE TABLE QueueItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL DEFAULT 0,
                FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL,
                PostProcessing INTEGER NOT NULL, PauseUntil TEXT, ArrDownloadId TEXT, ContentGroupKey TEXT, IndexerName TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);

            CREATE TABLE HistoryItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL,
                DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT, FileName TEXT NOT NULL, JobName TEXT NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, DownloadDirId TEXT, ArrDownloadId TEXT, ContentGroupKey TEXT,
                IndexerName TEXT, LastPlayedAt INTEGER, NzbBlobId TEXT);

            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            CREATE UNIQUE INDEX IX_Accounts_SingleAdmin ON Accounts (Type) WHERE Type = 1;

            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT, JobName TEXT, NzbFileName TEXT);
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY (DateStartInclusive, DateEndExclusive, Result, RepairStatus));
            """);
        return conn;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
