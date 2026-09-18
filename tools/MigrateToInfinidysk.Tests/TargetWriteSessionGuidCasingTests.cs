using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Round-20 regression, PART 1: infinidysk's own EF migration
/// 20260820160000_Normalize-Guid-Text-Casing uppercases every GUID-shaped TEXT column it knows
/// about and is already recorded as applied on a freshly-created target schema BEFORE this tool
/// ever writes a row. Microsoft.Data.Sqlite binds Guid-typed EF parameters as uppercase hex, and
/// SQLite TEXT comparisons are case-sensitive - a target row written with the source database's
/// original (frequently lowercase) casing is invisible to every later EF Guid-typed lookup.
/// Confirmed live: 1,313/1,313 DavMultipartFiles.Id rows lowercase, breaking both the blobstore
/// migration background job and legacy-playback fallback (DavDatabaseClient.cs:208) for every
/// affected row.
///
/// TargetWriteSession now uppercases every GUID-shaped column it writes (SqliteTargetWriter.cs's
/// private G() helper). This test drives every Insert* method with lowercase-text source Guids
/// and asserts every affected column comes out uppercase in the target row, then proves the fix
/// actually matters (not just cosmetic) by running a simulated EF-style typed-parameter lookup -
/// exactly the shape EF itself generates (a bound parameter, not a string-literal WHERE clause) -
/// against the written row and confirming it resolves.
/// </summary>
public class TargetWriteSessionGuidCasingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"guidcasing-{Guid.NewGuid()}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void InsertDavItem_LowercaseSourceGuids_WritesUppercaseIdParentIdAndHistoryItemId()
    {
        using var conn = BuildFixture();
        var id = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());
        var parentId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());
        var historyItemId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());

        using (var session = new SqliteTargetWriter.TargetWriteSession(conn))
        {
            // The parent row has to exist for the FK-deferred commit to succeed, but its own
            // casing is irrelevant to this test - only the child row's own uppercasing matters.
            session.InsertDavItem(new TargetDavItem(
                parentId, parentId.ToString()[..5], 1700000000, null, "parent", null, 1, 100,
                "/parent", null, null, null, HealthRepairPending: false, null));
            session.InsertDavItem(new TargetDavItem(
                id, id.ToString()[..5], 1700000000, parentId, "child.mkv", 1000, 2, 203,
                "/parent/child.mkv", null, null, null, HealthRepairPending: false, historyItemId));
            session.Commit();
        }

        AssertColumnIsCanonicalUppercase(conn, "DavItems", "Id", id);
        AssertColumnIsCanonicalUppercase(conn, "DavItems", "ParentId", parentId);
        AssertColumnIsCanonicalUppercase(conn, "DavItems", "HistoryItemId", historyItemId);

        // Simulated EF-style typed-parameter lookup: a real Guid value bound as a parameter,
        // never a string-literal WHERE clause - this is exactly the shape
        // Microsoft.Data.Sqlite/EF Core generate for a Guid-typed column comparison, and it's
        // exactly what failed against the pre-fix lowercase rows in production.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM DavItems WHERE Id = $id";
        cmd.Parameters.Add(new SqliteParameter("$id", id)); // Guid-typed, not id.ToString()
        Assert.Equal("child.mkv", cmd.ExecuteScalar());
    }

    [Fact]
    public void InsertDavNzbFileAndDavMultipartFile_LowercaseSourceGuid_WritesUppercaseId()
    {
        using var conn = BuildFixture();
        var davItemId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());
        var nzbId = davItemId; // shares Id with its DavItems row, like the real schema's FK
        var multipartId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());

        using (var session = new SqliteTargetWriter.TargetWriteSession(conn))
        {
            session.InsertDavItem(new TargetDavItem(
                davItemId, davItemId.ToString()[..5], 1700000000, null, "movie.mkv", 5000, 3, 300,
                "/movie.mkv", null, null, null, HealthRepairPending: false, null));
            session.InsertDavItem(new TargetDavItem(
                multipartId, multipartId.ToString()[..5], 1700000000, null, "part.mkv", 5000, 3, 300,
                "/part.mkv", null, null, null, HealthRepairPending: false, null));
            session.InsertDavNzbFile(new TargetDavNzbFile(nzbId, "[\"seg-1\"]"));
            session.InsertDavMultipartFile(new TargetDavMultipartFile(multipartId, "{}"));
            session.Commit();
        }

        AssertColumnIsCanonicalUppercase(conn, "DavNzbFiles", "Id", nzbId);
        AssertColumnIsCanonicalUppercase(conn, "DavMultipartFiles", "Id", multipartId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DavMultipartFiles WHERE Id = $id";
        cmd.Parameters.Add(new SqliteParameter("$id", multipartId));
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void InsertQueueItemAndQueueNzbContents_LowercaseSourceGuid_WritesUppercaseId()
    {
        using var conn = BuildFixture();
        var queueId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());

        using (var session = new SqliteTargetWriter.TargetWriteSession(conn))
        {
            session.InsertQueueItem(new TargetQueueItem(
                queueId, 1700000000, 0, "file.nzb", "job", 1000, 900, "movies", 0, 0, null));
            session.InsertQueueNzbContents(new TargetQueueNzbContents(queueId, "<nzb/>"));
            session.Commit();
        }

        AssertColumnIsCanonicalUppercase(conn, "QueueItems", "Id", queueId);
        AssertColumnIsCanonicalUppercase(conn, "QueueNzbContents", "Id", queueId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM QueueNzbContents WHERE Id = $id";
        cmd.Parameters.Add(new SqliteParameter("$id", queueId));
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void InsertHistoryItemAndHealthCheckResult_LowercaseSourceGuids_WritesUppercaseIdAndFkColumns()
    {
        using var conn = BuildFixture();
        var historyId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());
        var downloadDirId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());
        var davItemId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());
        var healthCheckId = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());

        using (var session = new SqliteTargetWriter.TargetWriteSession(conn))
        {
            session.InsertHistoryItem(new TargetHistoryItem(
                historyId, 1700000000, "movies", 1, 0, null, "file.nzb", "job", 900, downloadDirId));
            session.InsertDavItem(new TargetDavItem(
                davItemId, davItemId.ToString()[..5], 1700000000, null, "movie.mkv", 5000, 2, 201,
                "/movie.mkv", null, null, null, HealthRepairPending: false, null));
            session.InsertHealthCheckResult(new TargetHealthCheckResult(
                healthCheckId, 1700000000, davItemId, "/movie.mkv", 0, 0, null));
            session.Commit();
        }

        AssertColumnIsCanonicalUppercase(conn, "HistoryItems", "Id", historyId);
        AssertColumnIsCanonicalUppercase(conn, "HistoryItems", "DownloadDirId", downloadDirId);
        AssertColumnIsCanonicalUppercase(conn, "HealthCheckResults", "Id", healthCheckId);
        AssertColumnIsCanonicalUppercase(conn, "HealthCheckResults", "DavItemId", davItemId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path FROM HealthCheckResults WHERE DavItemId = $id";
        cmd.Parameters.Add(new SqliteParameter("$id", davItemId));
        Assert.Equal("/movie.mkv", cmd.ExecuteScalar());
    }

    private static void AssertColumnIsCanonicalUppercase(SqliteConnection conn, string table, string column, Guid expectedId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {table} WHERE {column} = $expected COLLATE BINARY";
        cmd.Parameters.AddWithValue("$expected", expectedId.ToString().ToUpperInvariant());
        var actual = Assert.IsType<string>(cmd.ExecuteScalar());
        Assert.Equal(expectedId.ToString().ToUpperInvariant(), actual);
    }

    private SqliteConnection BuildFixture()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL DEFAULT 0,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL DEFAULT 0, HistoryItemId TEXT);

            CREATE TABLE DavNzbFiles (
                Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES DavItems (Id) ON DELETE CASCADE);
            CREATE TABLE DavMultipartFiles (
                Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES DavItems (Id) ON DELETE CASCADE);

            CREATE TABLE QueueItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL DEFAULT 0,
                FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL,
                PostProcessing INTEGER NOT NULL, PauseUntil TEXT);
            CREATE TABLE QueueNzbContents (
                Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES QueueItems (Id) ON DELETE CASCADE);

            CREATE TABLE HistoryItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL,
                DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT, FileName TEXT NOT NULL, JobName TEXT NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, DownloadDirId TEXT);

            CREATE TABLE HealthCheckResults (
                Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL,
                Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT);
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }
}
