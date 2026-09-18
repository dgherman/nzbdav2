using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Round-16 regression: real production failure found via live SSH inspection of a user's
/// infinidysk target DB - "UNIQUE constraint failed: DavItems.Path". infinidysk seeds 5
/// fixed-GUID structural root DavItems rows on first startup (Root=/, NzbFolder=/nzbs,
/// ContentFolder=/content, SymlinkFolder=/completed-symlinks, IdsFolder=/.ids). nzbdav2's source
/// DB has its own root rows at the SAME Paths but DIFFERENT Ids. StreamingMigrator did a plain
/// INSERT with no conflict handling, so it collided on DavItems' UNIQUE(Path) index the first
/// time it tried to insert the source's own root row.
///
/// Also confirmed live: users can create real content in infinidysk BEFORE ever running this
/// tool (observed: /content/uncategorized, user-created, timestamped after infinidysk's first
/// boot but before migration), so Path collisions aren't limited to the 5 known scaffold roots -
/// any Path the target already has is a possible collision, and that target data must never be
/// clobbered or lost.
/// </summary>
public class StreamingMigratorPathCollisionTests : IDisposable
{
    private static readonly Guid SourceRootId = Guid.Parse("00000000-0000-0000-0000-000000000000");
    private static readonly Guid SourceNzbFolderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SourceContentFolderId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid SourceSymlinkFolderId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid SourceIdsFolderId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    // infinidysk's own scaffold roots use different Ids than nzbdav2's at the same 5 Paths -
    // that's the whole bug. Random, not the well-known 0000-0004 values, matching the real
    // report exactly (same ids on both sides would instead violate the Id PRIMARY KEY, a
    // different error than the one reported).
    private static readonly Guid TargetRootId = Guid.NewGuid();
    private static readonly Guid TargetNzbFolderId = Guid.NewGuid();
    private static readonly Guid TargetContentFolderId = Guid.NewGuid();
    private static readonly Guid TargetSymlinkFolderId = Guid.NewGuid();
    private static readonly Guid TargetIdsFolderId = Guid.NewGuid();

    private readonly string _sourceDbPath = Path.Combine(Path.GetTempPath(), $"nzbdav2-pathcollision-{Guid.NewGuid()}.sqlite");
    private readonly string _targetDbPath = Path.Combine(Path.GetTempPath(), $"infinidysk-pathcollision-{Guid.NewGuid()}.sqlite");
    private readonly string _archivePath = Path.Combine(Path.GetTempPath(), $"archive-pathcollision-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_sourceDbPath);
        File.Delete(_targetDbPath);
        if (File.Exists(_archivePath)) File.Delete(_archivePath);
    }

    [Fact]
    public void Run_ScaffoldRootPathCollision_KeepsTargetRoots_ReparentsChildContent_NoCrash()
    {
        var movieId = Guid.NewGuid();
        BuildSourceFixtureWithScaffoldRootsAndOneChild(movieId);

        using var target = BuildTargetFixtureWithPreSeededScaffoldRoots();
        using var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();

        var result = StreamingMigrator.Run(sourceConn, target, _archivePath, new MigrationOptions(null));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Informational, not a warning: this collision is expected on every real-world run.
        Assert.NotNull(result.Infos);
        Assert.Contains(result.Infos!, i => i.Contains('5') && i.Contains("scaffold"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("/content", StringComparison.Ordinal));

        // Target's own scaffold roots survive untouched, at their OWN ids - never overwritten.
        AssertRowExists(target, TargetRootId, "/");
        AssertRowExists(target, TargetNzbFolderId, "/nzbs");
        AssertRowExists(target, TargetContentFolderId, "/content");
        AssertRowExists(target, TargetSymlinkFolderId, "/completed-symlinks");
        AssertRowExists(target, TargetIdsFolderId, "/.ids");

        // The source's own root rows were never inserted (no PK/second Path collision, no crash).
        AssertRowMissing(target, SourceRootId);
        AssertRowMissing(target, SourceNzbFolderId);
        AssertRowMissing(target, SourceContentFolderId);
        AssertRowMissing(target, SourceSymlinkFolderId);
        AssertRowMissing(target, SourceIdsFolderId);

        // The child content that hung off the colliding /content root is not orphaned: it must
        // land re-parented onto the TARGET's existing content folder Id (requirement 3).
        using var cmd = target.CreateCommand();
        cmd.CommandText = "SELECT ParentId FROM DavItems WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", movieId.ToString());
        Assert.Equal(TargetContentFolderId.ToString(), cmd.ExecuteScalar());

        // And its multipart payload made it across too (movie.mkv's own Path never collided).
        cmd.CommandText = "SELECT COUNT(*) FROM DavMultipartFiles WHERE Id = $id";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Run_GenuineUserContentPathCollision_WarnsAndKeepsTargetRow_ReparentsChildContent_NoCrash()
    {
        var existingUncategorizedId = Guid.NewGuid(); // target's own, user-created after first boot
        var sourceUncategorizedId = Guid.NewGuid();    // nzbdav2's independently-created folder, same Path
        var deepFileId = Guid.NewGuid();

        BuildSourceFixtureWithScaffoldRootsAndOneChild(movieId: null, extraFolder: (sourceUncategorizedId, deepFileId));

        using var target = BuildTargetFixtureWithPreSeededScaffoldRoots(extraUserFolder: existingUncategorizedId);
        using var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();

        var result = StreamingMigrator.Run(sourceConn, target, _archivePath, new MigrationOptions(null));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Genuine collision (not one of the 5 scaffold roots): reported as a WARNING, distinct
        // from the scaffold-root informational message.
        Assert.Contains(result.Warnings, w => w.Contains("/content/uncategorized", StringComparison.Ordinal));

        // Target's pre-existing, user-created row survives completely untouched.
        AssertRowExists(target, existingUncategorizedId, "/content/uncategorized");

        // The source's independently-created folder at the same Path was never inserted - the
        // target's real content was not clobbered.
        AssertRowMissing(target, sourceUncategorizedId);

        // The file that hung off the colliding source folder is re-parented onto the TARGET's
        // existing row at that path, not dropped.
        using var cmd = target.CreateCommand();
        cmd.CommandText = "SELECT ParentId FROM DavItems WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", deepFileId.ToString());
        Assert.Equal(existingUncategorizedId.ToString(), cmd.ExecuteScalar());

        cmd.CommandText = "SELECT COUNT(*) FROM DavMultipartFiles WHERE Id = $id";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    private static void AssertRowExists(SqliteConnection conn, Guid id, string expectedPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path FROM DavItems WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        Assert.Equal(expectedPath, cmd.ExecuteScalar());
    }

    private static void AssertRowMissing(SqliteConnection conn, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DavItems WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    /// <summary>
    /// Source fixture: the 5 nzbdav2-side scaffold roots at their real fixed Ids/Paths, plus
    /// either (a) a movie.mkv NzbFile directly under /content (movieId case), or (b) an
    /// independently-created /content/uncategorized folder with one nested file under it
    /// (extraFolder case) - both routed through the DavNzbFiles-wrap-into-DavMultipartFiles path
    /// (a real FileSize, no obfuscation key involved) so this test exercises the collision logic
    /// in isolation from ObfuscationDetector's own skip path.
    /// </summary>
    private void BuildSourceFixtureWithScaffoldRootsAndOneChild(Guid? movieId, (Guid folderId, Guid fileId)? extraFolder = null)
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

        InsertDavItem(conn, SourceRootId, null, "/", "/", 1);
        InsertDavItem(conn, SourceNzbFolderId, SourceRootId, "nzbs", "/nzbs", 1);
        InsertDavItem(conn, SourceContentFolderId, SourceRootId, "content", "/content", 1);
        InsertDavItem(conn, SourceSymlinkFolderId, SourceRootId, "completed-symlinks", "/completed-symlinks", 2);
        InsertDavItem(conn, SourceIdsFolderId, SourceRootId, ".ids", "/.ids", 5);

        if (movieId.HasValue)
        {
            InsertDavItem(conn, movieId.Value, SourceContentFolderId, "movie.mkv", "/content/movie.mkv", 3, fileSize: 5000);
            InsertDavNzbFile(conn, movieId.Value, ["seg-1"]);
        }

        if (extraFolder.HasValue)
        {
            var (folderId, fileId) = extraFolder.Value;
            InsertDavItem(conn, folderId, SourceContentFolderId, "uncategorized", "/content/uncategorized", 1);
            InsertDavItem(conn, fileId, folderId, "deep.mkv", "/content/uncategorized/deep.mkv", 3, fileSize: 3000);
            InsertDavNzbFile(conn, fileId, ["seg-deep-1"]);
        }
    }

    private static void InsertDavItem(SqliteConnection conn, Guid id, Guid? parentId, string name, string path, int type, long? fileSize = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, Path)
            VALUES ($id, $prefix, '2026-01-01 00:00:00', $parentId, $name, $fileSize, $type, $path)
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$prefix", id.ToString()[..5]);
        cmd.Parameters.AddWithValue("$parentId", (object?)parentId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$fileSize", (object?)fileSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    private static void InsertDavNzbFile(SqliteConnection conn, Guid id, string[] segmentIds)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO DavNzbFiles (Id, SegmentIds, SegmentFallbacks) VALUES ($id, $segIds, NULL)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$segIds", System.Text.Json.JsonSerializer.Serialize(segmentIds));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Target fixture: real FOREIGN KEY constraints (DavMultipartFiles/DavRarFiles/DavNzbFiles ->
    /// DavItems, matching round 14) AND a real UNIQUE index on DavItems.Path (matching
    /// infinidysk's actual schema - the exact constraint the live report's "UNIQUE constraint
    /// failed: DavItems.Path" error came from). Pre-seeded with infinidysk's own 5 scaffold roots
    /// at DIFFERENT Ids than the source uses, plus optionally one more real user-created row, to
    /// reproduce the live bug's starting state.
    /// </summary>
    private SqliteConnection BuildTargetFixtureWithPreSeededScaffoldRoots(Guid? extraUserFolder = null)
    {
        var conn = new SqliteConnection($"Data Source={_targetDbPath};Mode=ReadWriteCreate");
        conn.Open();
        Execute(conn, """
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL DEFAULT 0,
                Path TEXT NOT NULL UNIQUE, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
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

        InsertTargetDavItem(conn, TargetRootId, null, "/", 1, 102);
        InsertTargetDavItem(conn, TargetNzbFolderId, TargetRootId, "/nzbs", 1, 103);
        InsertTargetDavItem(conn, TargetContentFolderId, TargetRootId, "/content", 1, 104);
        InsertTargetDavItem(conn, TargetSymlinkFolderId, TargetRootId, "/completed-symlinks", 1, 105);
        InsertTargetDavItem(conn, TargetIdsFolderId, TargetRootId, "/.ids", 1, 106);

        if (extraUserFolder.HasValue)
        {
            // A folder the user created independently, after infinidysk's first boot but before
            // migration - real content the migration must never clobber or lose.
            InsertTargetDavItem(conn, extraUserFolder.Value, TargetContentFolderId, "/content/uncategorized", 1, 101);
        }

        return conn;
    }

    private static void InsertTargetDavItem(SqliteConnection conn, Guid id, Guid? parentId, string path, int type, int subType)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, Type, SubType, Path)
            VALUES ($id, $prefix, '2026-01-01 00:00:00.000', $parentId, $name, $type, $subType, $path)
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$prefix", id.ToString()[..5]);
        cmd.Parameters.AddWithValue("$parentId", (object?)parentId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", path.Split('/').Last());
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$subType", subType);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
