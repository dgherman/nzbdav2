using System.Text.Json;
using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Round-19 regression: a real source nzbdav2 database was found with QueueNzbContents rows whose
/// Id (infinidysk shares QueueNzbContents.Id as the FK back to QueueItems.Id - see
/// QueueNzbContents.cs's QueueItem navigation) had no matching QueueItems row at all - QueueItems
/// was completely empty in that source database. Pre-existing dangling data in the source, not a
/// migrator ordering bug (unlike round 14/15's DavNzbFiles case, where the parent row DOES exist
/// but arrives in a later streaming pass). Inserting one of these verbatim hit the same
/// deferred-FK failure round 18 now diagnoses.
///
/// StreamingMigrator.Run's QueueNzbContents loop now checks each row's Id against the full set of
/// source QueueItems.Id (already read in full, just above, for the SortOrder backfill - see the
/// class doc comment for why QueueItems is the one deliberate non-streamed table) before
/// inserting. A row with no matching QueueItems.Id is skipped, archived (not silently dropped),
/// and counted - same treatment as every other unsupported/orphaned row in this tool.
/// </summary>
public class StreamingMigratorOrphanedQueueNzbContentsTests : IDisposable
{
    private readonly string _sourceDbPath = Path.Combine(Path.GetTempPath(), $"nzbdav2-orphanqueue-{Guid.NewGuid()}.sqlite");
    private readonly string _targetDbPath = Path.Combine(Path.GetTempPath(), $"infinidysk-orphanqueue-{Guid.NewGuid()}.sqlite");
    private readonly string _archivePath = Path.Combine(Path.GetTempPath(), $"archive-orphanqueue-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_sourceDbPath);
        File.Delete(_targetDbPath);
        if (File.Exists(_archivePath)) File.Delete(_archivePath);
    }

    [Fact]
    public void Run_QueueNzbContentsRowWithNoMatchingQueueItem_SkipsAndArchivesInsteadOfThrowing()
    {
        var wellFormedId = Guid.NewGuid();
        var orphanedId = Guid.NewGuid();
        BuildSourceFixture(wellFormedId, orphanedId);

        using var target = BuildTargetFixtureWithForeignKeys();
        using var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();

        var result = StreamingMigrator.Run(sourceConn, target, _archivePath, new MigrationOptions(null));

        // No FK exception - the whole point of the fix.
        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Counted correctly: 1 well-formed row copied, 1 orphaned row skipped and archived.
        var counts = result.Counts["QueueNzbContents"];
        Assert.Equal(1, counts.Copied);
        Assert.Equal(1, counts.Skipped);
        Assert.Equal(1, counts.Archived);

        // Not silently dropped: named in a warning.
        Assert.Contains(result.Warnings, w =>
            w.Contains(orphanedId.ToString()) && w.Contains("QueueNzbContents") && w.Contains("QueueItems"));

        // The well-formed row landed in the target; the orphaned one did not. Round 20: rows this
        // tool writes are now uppercase GUID text, so the lookup param has to match that casing.
        using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM QueueNzbContents WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", wellFormedId.ToString().ToUpperInvariant());
            Assert.Equal(1L, cmd.ExecuteScalar());

            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT COUNT(*) FROM QueueNzbContents WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", orphanedId.ToString().ToUpperInvariant());
            Assert.Equal(0L, cmd.ExecuteScalar());
        }

        // Recoverable, not lost: the orphaned row's Id and NzbContents are in the archive.
        using var archiveDoc = JsonDocument.Parse(File.ReadAllText(_archivePath));
        var orphanedArray = archiveDoc.RootElement.GetProperty("OrphanedQueueNzbContents");
        Assert.Equal(1, orphanedArray.GetArrayLength());
        Assert.Equal(orphanedId.ToString(), orphanedArray[0].GetProperty("Id").GetString());
    }

    private void BuildSourceFixture(Guid wellFormedQueueItemId, Guid orphanedQueueNzbContentsId)
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
            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT, Operation TEXT NOT NULL DEFAULT 'UNKNOWN');
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY (DateStartInclusive, DateEndExclusive, Result, RepairStatus));
            """);

        // A well-formed QueueItems + QueueNzbContents pair.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO QueueItems (Id, CreatedAt, FileName, JobName, NzbFileSize, TotalSegmentBytes, Category, Priority, PostProcessing)
                VALUES ($id, '2026-01-01 00:00:00', 'movie.nzb', 'movie', 1000, 900, 'movies', 0, 0)
                """;
            cmd.Parameters.AddWithValue("$id", wellFormedQueueItemId.ToString());
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO QueueNzbContents (Id, NzbContents) VALUES ($id, $contents)";
            cmd.Parameters.AddWithValue("$id", wellFormedQueueItemId.ToString());
            cmd.Parameters.AddWithValue("$contents", "<nzb>well-formed</nzb>");
            cmd.ExecuteNonQuery();
        }

        // An orphaned QueueNzbContents row - no QueueItems row with this Id anywhere in the
        // source. QueueItems is otherwise non-empty (the well-formed row above), matching the
        // real-world report where QueueItems was entirely empty but this is the more general case.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO QueueNzbContents (Id, NzbContents) VALUES ($id, $contents)";
            cmd.Parameters.AddWithValue("$id", orphanedQueueNzbContentsId.ToString());
            cmd.Parameters.AddWithValue("$contents", "<nzb>orphaned</nzb>");
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Real FK constraint - QueueNzbContents.Id REFERENCES QueueItems(Id) - matching infinidysk's
    /// actual schema (QueueNzbContents.cs's QueueItem navigation). Without this, the test would
    /// pass even against the pre-fix behavior on a target with no FK to violate.
    /// </summary>
    private SqliteConnection BuildTargetFixtureWithForeignKeys()
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

            CREATE TABLE DavNzbFiles (
                Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES DavItems (Id) ON DELETE CASCADE);
            CREATE TABLE DavMultipartFiles (
                Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES DavItems (Id) ON DELETE CASCADE);
            CREATE TABLE DavRarFiles (
                Id TEXT PRIMARY KEY, RarParts TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES DavItems (Id) ON DELETE CASCADE);

            CREATE TABLE QueueItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL DEFAULT 0,
                FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL,
                PostProcessing INTEGER NOT NULL, PauseUntil TEXT, ArrDownloadId TEXT, ContentGroupKey TEXT, IndexerName TEXT);
            CREATE TABLE QueueNzbContents (
                Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES QueueItems (Id) ON DELETE CASCADE);

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
