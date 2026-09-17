using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class SchemaGuardColumnCheckTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    public SchemaGuardColumnCheckTests() => _conn.Open();
    public void Dispose() => _conn.Dispose();

    [Fact]
    public void CheckTargetSchema_MissingFileBlobIdColumn_IsRejectedBeforeAnyWrite()
    {
        // Reproduces the review finding: a target fixture missing DavItems.FileBlobId entirely
        // previously passed CheckTarget (which only looked at __EFMigrationsHistory row ids)
        // and let --apply proceed.
        Execute("""
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL, HistoryItemId TEXT);
            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE QueueItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL, FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL, TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL, PostProcessing INTEGER NOT NULL, PauseUntil TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);
            CREATE TABLE HistoryItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL, DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT, FileName TEXT NOT NULL, JobName TEXT NOT NULL, TotalSegmentBytes INTEGER NOT NULL, DownloadDirId TEXT);
            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT);
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL);
            """);

        var result = SchemaGuard.CheckTargetSchema(_conn);

        Assert.False(result.IsValid);
        Assert.Contains("FileBlobId", result.ErrorMessage);
    }

    [Fact]
    public void CheckTargetSchema_MissingNzbBlobIdColumn_IsRejectedBeforeAnyWrite()
    {
        // Reviewer's round-3 repro: a fixture missing DavItems.NzbBlobId (distinct from
        // FileBlobId, checked separately above) previously passed and let --apply commit.
        Execute("""
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL, FileBlobId TEXT, HistoryItemId TEXT);
            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE QueueItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL, FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL, TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL, PostProcessing INTEGER NOT NULL, PauseUntil TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);
            CREATE TABLE HistoryItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL, DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT, FileName TEXT NOT NULL, JobName TEXT NOT NULL, TotalSegmentBytes INTEGER NOT NULL, DownloadDirId TEXT);
            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            CREATE UNIQUE INDEX IX_Accounts_SingleAdmin ON Accounts (Type) WHERE Type = 1;
            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT);
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL);
            """);

        var result = SchemaGuard.CheckTargetSchema(_conn);

        Assert.False(result.IsValid);
        Assert.Contains("NzbBlobId", result.ErrorMessage);
    }

    [Fact]
    public void CheckTargetSchema_MissingSingleAdminUniqueIndex_IsRejectedBeforeAnyWrite()
    {
        // Reviewer's round-3 repro: a fixture missing the IX_Accounts_SingleAdmin unique index
        // previously passed. Without that index, the tool's multi-admin safety check (which
        // relies on this constraint actually being enforced by the target DB) silently doesn't
        // protect anything.
        Execute("""
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL, FileBlobId TEXT, NzbBlobId TEXT, HistoryItemId TEXT);
            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE QueueItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL, FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL, TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL, PostProcessing INTEGER NOT NULL, PauseUntil TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);
            CREATE TABLE HistoryItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL, DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT, FileName TEXT NOT NULL, JobName TEXT NOT NULL, TotalSegmentBytes INTEGER NOT NULL, DownloadDirId TEXT);
            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT);
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL);
            """);

        var result = SchemaGuard.CheckTargetSchema(_conn);

        Assert.False(result.IsValid);
        Assert.Contains("IX_Accounts_SingleAdmin", result.ErrorMessage);
    }

    [Fact]
    public void CheckTargetSchema_MissingWholeTable_IsRejectedWithTableNamed()
    {
        Execute("CREATE TABLE DavItems (Id TEXT PRIMARY KEY);");

        var result = SchemaGuard.CheckTargetSchema(_conn);

        Assert.False(result.IsValid);
        Assert.Contains("QueueItems", result.ErrorMessage);
    }

    [Fact]
    public void CheckTargetSchema_AllRequiredTablesAndColumnsPresent_IsValid()
    {
        Execute("""
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL, FileBlobId TEXT, NzbBlobId TEXT, HistoryItemId TEXT);
            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE QueueItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL, FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL, TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL, PostProcessing INTEGER NOT NULL, PauseUntil TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);
            CREATE TABLE HistoryItems (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL, DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT, FileName TEXT NOT NULL, JobName TEXT NOT NULL, TotalSegmentBytes INTEGER NOT NULL, DownloadDirId TEXT);
            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            CREATE UNIQUE INDEX IX_Accounts_SingleAdmin ON Accounts (Type) WHERE Type = 1;
            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT);
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL);
            """);

        var result = SchemaGuard.CheckTargetSchema(_conn);

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
