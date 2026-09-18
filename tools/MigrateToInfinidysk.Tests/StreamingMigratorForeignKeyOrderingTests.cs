using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Round-14 regression: StreamingMigrator.Run streams and inserts DavMultipartFiles/DavRarFiles/
/// DavNzbFiles into the target DB in one pass, BEFORE the later pass that inserts DavItems (see
/// StreamingMigrator.Run - DavItems' own Type/SubType mapping depends on knowing which
/// multipart/nzb rows were skipped or wrapped first, so DavItems has to come after). infinidysk's
/// real schema declares DavMultipartFiles.Id/DavRarFiles.Id/DavNzbFiles.Id as a FOREIGN KEY
/// referencing DavItems.Id (confirmed via infinidysk's DavDatabaseContext.OnModelCreating -
/// HasForeignKey&lt;DavNzbFile&gt;/&lt;DavRarFile&gt;/&lt;DavMultipartFile&gt;(f =&gt; f.Id),
/// read-only reference), and Microsoft.Data.Sqlite enables `PRAGMA foreign_keys` ON by default -
/// so inserting a DavMultipartFiles row before its DavItems row exists throws "FOREIGN KEY
/// constraint failed" on a real target schema. None of the earlier fixture-based tests in this
/// project declare an actual FK constraint on these columns, which is why this regression wasn't
/// caught until a real user hit it on --apply.
///
/// The concrete trigger (confirmed by reading StreamingMigrator.Run): a DavNzbFiles row with a
/// valid source DavItems.FileSize gets wrapped into a DavMultipartFiles target row (so its
/// SegmentFallbacks land somewhere infinidysk reads at playback - see round 8/9), and that
/// target.InsertDavMultipartFile call happens well before the later DavItems insert pass.
/// </summary>
public class StreamingMigratorForeignKeyOrderingTests : IDisposable
{
    private readonly string _sourceDbPath = Path.Combine(Path.GetTempPath(), $"nzbdav2-fktest-{Guid.NewGuid()}.sqlite");
    private readonly string _targetDbPath = Path.Combine(Path.GetTempPath(), $"infinidysk-fktest-{Guid.NewGuid()}.sqlite");
    private readonly string _archivePath = Path.Combine(Path.GetTempPath(), $"archive-fktest-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_sourceDbPath);
        File.Delete(_targetDbPath);
        if (File.Exists(_archivePath)) File.Delete(_archivePath);
    }

    [Fact]
    public void Run_Apply_AgainstTargetWithRealForeignKeyConstraints_DoesNotViolateInsertOrder()
    {
        var nzbId = Guid.NewGuid();
        BuildSourceFixture(nzbId);

        using var target = BuildTargetFixtureWithForeignKeys();
        using var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();

        var result = StreamingMigrator.Run(sourceConn, target, _archivePath, new MigrationOptions(null));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(1, result.Counts["DavItems"].Copied);
        Assert.Equal(1, result.Counts["DavMultipartFiles"].Copied); // the NZB-with-fallbacks wrap path

        using var cmd = target.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DavItems WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", nzbId.ToString());
        Assert.Equal(1L, cmd.ExecuteScalar());

        cmd.CommandText = "SELECT COUNT(*) FROM DavMultipartFiles WHERE Id = $id";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    private void BuildSourceFixture(Guid nzbId)
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

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, Path)
            VALUES ($id, $prefix, '2026-01-01 00:00:00', NULL, 'movie.mkv', 5000, 3, '/content/movie.mkv')
            """;
        cmd.Parameters.AddWithValue("$id", nzbId.ToString());
        cmd.Parameters.AddWithValue("$prefix", nzbId.ToString()[..5]);
        cmd.ExecuteNonQuery();

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "INSERT INTO DavNzbFiles (Id, SegmentIds, SegmentFallbacks) VALUES ($id, $segIds, $fallbacks)";
        cmd2.Parameters.AddWithValue("$id", nzbId.ToString());
        cmd2.Parameters.AddWithValue("$segIds", """["seg-1","seg-2"]""");
        cmd2.Parameters.AddWithValue("$fallbacks", """{"1":["fallback-for-seg-2"]}""");
        cmd2.ExecuteNonQuery();
    }

    /// <summary>
    /// Real FK constraints - DavMultipartFiles.Id/DavRarFiles.Id/DavNzbFiles.Id REFERENCES
    /// DavItems(Id) - matching infinidysk's actual schema (see class doc comment). Without these,
    /// this test would pass even against the pre-fix insert ordering, since SQLite has nothing to
    /// enforce - this is exactly why earlier tests in this project didn't catch the regression.
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
