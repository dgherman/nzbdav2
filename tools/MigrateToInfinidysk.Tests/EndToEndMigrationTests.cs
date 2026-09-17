using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// End-to-end: builds a real nzbdav2-shaped fixture SQLite DB and a real
/// already-fully-migrated infinidysk-shaped fixture SQLite DB (schema transcribed from both
/// projects' EF Core model snapshots - read-only reference, no source referenced), runs the
/// full SqliteSourceReader -> Migrator -> SqliteTargetWriter pipeline against them with --apply
/// semantics, and asserts against the actual resulting rows and the target's real constraints
/// (e.g. the filtered unique index on Accounts.Type), not just in-memory mocks.
/// </summary>
public class EndToEndMigrationTests : IDisposable
{
    private readonly string _sourceDbPath = Path.Combine(Path.GetTempPath(), $"nzbdav2-fixture-{Guid.NewGuid()}.sqlite");
    private readonly string _targetDbPath = Path.Combine(Path.GetTempPath(), $"infinidysk-fixture-{Guid.NewGuid()}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_sourceDbPath);
        File.Delete(_targetDbPath);
    }

    [Fact]
    public void FullPipeline_MigratesFixtureNzbdav2DbIntoFreshInfinidyskDb_WithoutViolatingConstraints()
    {
        var rootId = Guid.Parse("00000000-0000-0000-0000-000000000000");
        var contentFolderId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var movieItemId = Guid.NewGuid();
        var obfuscatedItemId = Guid.NewGuid();
        var queueIdA = Guid.NewGuid();
        var queueIdB = Guid.NewGuid();

        using (var source = CreateSourceFixture())
        {
            InsertSourceDavItem(source, rootId, null, "/", "/", 1);
            InsertSourceDavItem(source, contentFolderId, rootId, "content", "/content", 1);
            InsertSourceDavItem(source, movieItemId, contentFolderId, "movie.mkv", "/content/movie.mkv", 6);
            InsertSourceDavItem(source, obfuscatedItemId, contentFolderId, "obfuscated.mkv", "/content/obfuscated.mkv", 6);

            InsertSourceDavMultipartFile(source, movieItemId, obfuscationKeyHex: null);
            InsertSourceDavMultipartFile(source, obfuscatedItemId, obfuscationKeyHex: "B041C2CE");

            InsertSourceQueueItem(source, queueIdA, "a.mkv", priority: 0, createdAtUnix: 1700000000);
            InsertSourceQueueItem(source, queueIdB, "b.mkv", priority: 0, createdAtUnix: 1700000100);

            InsertSourceAccount(source, type: 1, username: "alice");
            InsertSourceAccount(source, type: 1, username: "bob");
            InsertSourceAccount(source, type: 2, username: "webdav");

            InsertSourceConfigItem(source, "api.key", "the-api-key");
            InsertSourceConfigItem(source, "usenet.host", "news.example.com");
        }

        using var target = CreateTargetFixture();

        using (var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly"))
        {
            sourceConn.Open();
            var sourceMigrationCheck = SchemaGuard.CheckSource(SqliteSourceReader.ReadMigrationHistory(sourceConn));
            Assert.True(sourceMigrationCheck.IsValid, sourceMigrationCheck.ErrorMessage);

            var snapshot = SqliteSourceReader.Read(sourceConn);
            var result = Migrator.Run(snapshot, new MigrationOptions(RequestedAdminUsername: "bob"));

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Single(result.Archive.SkippedObfuscatedRows);

            SqliteTargetWriter.Apply(target, result);
        }

        // Assert against the real target DB, including its real constraints.
        AssertScalar(target, "SELECT COUNT(*) FROM Accounts WHERE Type = 1", 1L);
        AssertScalar(target, "SELECT Username FROM Accounts WHERE Type = 1", "bob");
        AssertScalar(target, "SELECT COUNT(*) FROM Accounts", 2L);

        AssertScalar(target, $"SELECT Type FROM DavItems WHERE Id = '{movieItemId}'", 2L);
        AssertScalar(target, $"SELECT SubType FROM DavItems WHERE Id = '{movieItemId}'", 203L);
        AssertScalar(target, $"SELECT SubType FROM DavItems WHERE Id = '{contentFolderId}'", 104L);

        AssertScalar(target, $"SELECT COUNT(*) FROM DavMultipartFiles WHERE Id = '{movieItemId}'", 1L);
        AssertScalar(target, $"SELECT COUNT(*) FROM DavMultipartFiles WHERE Id = '{obfuscatedItemId}'", 0L);

        AssertScalar(target, $"SELECT SortOrder FROM QueueItems WHERE Id = '{queueIdA}'", 1024L);
        AssertScalar(target, $"SELECT SortOrder FROM QueueItems WHERE Id = '{queueIdB}'", 2048L);

        AssertScalar(target, "SELECT COUNT(*) FROM ConfigItems WHERE ConfigName = 'api.key'", 1L);
        AssertScalar(target, "SELECT COUNT(*) FROM ConfigItems WHERE ConfigName = 'usenet.host'", 0L);
    }

    [Fact]
    public void FullPipeline_MultipleAdminsWithoutSelection_RefusesAndWritesNothing()
    {
        using (var source = CreateSourceFixture())
        {
            InsertSourceAccount(source, type: 1, username: "alice");
            InsertSourceAccount(source, type: 1, username: "bob");
        }

        using var target = CreateTargetFixture();

        using var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();
        var snapshot = SqliteSourceReader.Read(sourceConn);
        var result = Migrator.Run(snapshot, new MigrationOptions(RequestedAdminUsername: null));

        Assert.False(result.Success);

        var ex = Record.Exception(() => SqliteTargetWriter.Apply(target, result));
        Assert.IsType<InvalidOperationException>(ex);
        AssertScalar(target, "SELECT COUNT(*) FROM Accounts", 0L);
    }

    private static void AssertScalar(SqliteConnection conn, string sql, object expected)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var actual = cmd.ExecuteScalar();
        Assert.Equal(expected, actual);
    }

    private SqliteConnection CreateSourceFixture()
    {
        var conn = new SqliteConnection($"Data Source={_sourceDbPath}");
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
        return conn;
    }

    private SqliteConnection CreateTargetFixture()
    {
        var conn = new SqliteConnection($"Data Source={_targetDbPath}");
        conn.Open();
        Execute(conn, """
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO __EFMigrationsHistory VALUES ('20260129182923_Update-DavItems-Type-And-SubType', '10.0.11');
            INSERT INTO __EFMigrationsHistory VALUES ('20260731171110_Add-SingleAdmin-UniqueIndex', '10.0.11');
            INSERT INTO __EFMigrationsHistory VALUES ('20260817160000_Add-QueueItem-SortOrder', '10.0.11');

            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt INTEGER NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL DEFAULT 0,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL DEFAULT 0, FileBlobId TEXT, HistoryItemId TEXT, NzbBlobId TEXT,
                ArrDownloadId TEXT, GeneratedStrmOutputRoot TEXT, GeneratedStrmPath TEXT, GeneratedStrmTarget TEXT,
                GeneratedSymlinkOutputRoot TEXT, GeneratedSymlinkPath TEXT, GeneratedSymlinkTarget TEXT);
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, Type, SubType, Path)
                VALUES ('00000000-0000-0000-0000-000000000000', '00000', 0, NULL, '/', 1, 102, '/');
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, Type, SubType, Path)
                VALUES ('00000000-0000-0000-0000-000000000002', '00000', 0, '00000000-0000-0000-0000-000000000000', 'content', 1, 104, '/content');

            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE DavRarFiles (Id TEXT PRIMARY KEY, RarParts TEXT NOT NULL);

            CREATE TABLE QueueItems (
                Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, SortOrder INTEGER NOT NULL DEFAULT 0,
                FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL,
                PostProcessing INTEGER NOT NULL, PauseUntil INTEGER, ArrDownloadId TEXT, ContentGroupKey TEXT, IndexerName TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);

            CREATE TABLE HistoryItems (
                Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL,
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

    private static void InsertSourceDavItem(SqliteConnection conn, Guid id, Guid? parentId, string name, string path, int type)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, Type, Path)
            VALUES ($Id, $IdPrefix, '2026-01-01 00:00:00', $ParentId, $Name, $Type, $Path)
            """;
        cmd.Parameters.AddWithValue("$Id", id.ToString());
        cmd.Parameters.AddWithValue("$IdPrefix", id.ToString()[..5]);
        cmd.Parameters.AddWithValue("$ParentId", (object?)parentId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Name", name);
        cmd.Parameters.AddWithValue("$Type", type);
        cmd.Parameters.AddWithValue("$Path", path);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSourceDavMultipartFile(SqliteConnection conn, Guid id, string? obfuscationKeyHex)
    {
        var obfuscationKeyJson = obfuscationKeyHex == null
            ? "null"
            : System.Text.Json.JsonSerializer.Serialize(Convert.FromHexString(obfuscationKeyHex));
        var metadataJson = $$"""
            {"AesParams":null,"ObfuscationKey":{{obfuscationKeyJson}},"FileParts":[
                {"SegmentIds":["seg-1","seg-2"],
                 "SegmentIdByteRange":{"StartInclusive":0,"EndExclusive":100},
                 "FilePartByteRange":{"StartInclusive":0,"EndExclusive":100},
                 "SegmentFallbacks":null,"SegmentSizes":null}
            ]}
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO DavMultipartFiles (Id, Metadata) VALUES ($Id, $Metadata)";
        cmd.Parameters.AddWithValue("$Id", id.ToString());
        cmd.Parameters.AddWithValue("$Metadata", metadataJson);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSourceQueueItem(SqliteConnection conn, Guid id, string fileName, int priority, long createdAtUnix)
    {
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(createdAtUnix).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO QueueItems (Id, CreatedAt, FileName, JobName, NzbFileSize, TotalSegmentBytes, Category, Priority, PostProcessing)
            VALUES ($Id, $CreatedAt, $FileName, $FileName, 1000, 1000, 'movies', $Priority, 0)
            """;
        cmd.Parameters.AddWithValue("$Id", id.ToString());
        cmd.Parameters.AddWithValue("$CreatedAt", createdAt);
        cmd.Parameters.AddWithValue("$FileName", fileName);
        cmd.Parameters.AddWithValue("$Priority", priority);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSourceAccount(SqliteConnection conn, int type, string username)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Accounts (Type, Username, PasswordHash, RandomSalt) VALUES ($Type, $Username, 'hash', 'salt')";
        cmd.Parameters.AddWithValue("$Type", type);
        cmd.Parameters.AddWithValue("$Username", username);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSourceConfigItem(SqliteConnection conn, string name, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES ($Name, $Value)";
        cmd.Parameters.AddWithValue("$Name", name);
        cmd.Parameters.AddWithValue("$Value", value);
        cmd.ExecuteNonQuery();
    }
}
