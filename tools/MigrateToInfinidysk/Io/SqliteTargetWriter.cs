using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Applies a MigrationResult to an already-fully-migrated infinidysk db.sqlite. Never touches
/// __EFMigrationsHistory or any other infinidysk-internal bookkeeping table. Runs the whole
/// write in one transaction so a failure leaves the target untouched.
/// </summary>
public static class SqliteTargetWriter
{
    public static void Apply(SqliteConnection conn, MigrationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException("Refusing to apply a failed MigrationResult.");

        using var tx = conn.BeginTransaction();

        foreach (var item in result.DavItems)
        {
            Execute(conn, tx, """
                INSERT INTO DavItems
                    (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path,
                     ReleaseDate, LastHealthCheck, NextHealthCheck, HealthRepairPending, HistoryItemId)
                VALUES
                    ($Id, $IdPrefix, $CreatedAt, $ParentId, $Name, $FileSize, $Type, $SubType, $Path,
                     $ReleaseDate, $LastHealthCheck, $NextHealthCheck, $HealthRepairPending, $HistoryItemId)
                ON CONFLICT(Id) DO UPDATE SET
                    Type = excluded.Type, SubType = excluded.SubType
                """,
                ("$Id", item.Id.ToString()), ("$IdPrefix", item.IdPrefix), ("$CreatedAt", item.CreatedAtUnixSeconds),
                ("$ParentId", (object?)item.ParentId?.ToString() ?? DBNull.Value), ("$Name", item.Name),
                ("$FileSize", (object?)item.FileSize ?? DBNull.Value), ("$Type", item.Type), ("$SubType", item.SubType),
                ("$Path", item.Path), ("$ReleaseDate", (object?)item.ReleaseDateUnixSeconds ?? DBNull.Value),
                ("$LastHealthCheck", (object?)item.LastHealthCheckUnixSeconds ?? DBNull.Value),
                ("$NextHealthCheck", (object?)item.NextHealthCheckUnixSeconds ?? DBNull.Value),
                ("$HealthRepairPending", item.HealthRepairPending ? 1 : 0),
                ("$HistoryItemId", (object?)item.HistoryItemId?.ToString() ?? DBNull.Value));
        }

        foreach (var f in result.DavNzbFiles)
        {
            Execute(conn, tx, "INSERT INTO DavNzbFiles (Id, SegmentIds) VALUES ($Id, $SegmentIds)",
                ("$Id", f.Id.ToString()), ("$SegmentIds", f.SegmentIdsJson));
        }

        foreach (var f in result.DavMultipartFiles)
        {
            Execute(conn, tx, "INSERT INTO DavMultipartFiles (Id, Metadata) VALUES ($Id, $Metadata)",
                ("$Id", f.Id.ToString()), ("$Metadata", f.MetadataJson));
        }

        foreach (var q in result.QueueItems)
        {
            Execute(conn, tx, """
                INSERT INTO QueueItems
                    (Id, CreatedAt, SortOrder, FileName, JobName, NzbFileSize, TotalSegmentBytes,
                     Category, Priority, PostProcessing, PauseUntil)
                VALUES
                    ($Id, $CreatedAt, $SortOrder, $FileName, $JobName, $NzbFileSize, $TotalSegmentBytes,
                     $Category, $Priority, $PostProcessing, $PauseUntil)
                """,
                ("$Id", q.Id.ToString()), ("$CreatedAt", q.CreatedAtUnixSeconds), ("$SortOrder", q.SortOrder),
                ("$FileName", q.FileName), ("$JobName", q.JobName), ("$NzbFileSize", q.NzbFileSize),
                ("$TotalSegmentBytes", q.TotalSegmentBytes), ("$Category", q.Category), ("$Priority", q.Priority),
                ("$PostProcessing", q.PostProcessing), ("$PauseUntil", (object?)q.PauseUntilUnixSeconds ?? DBNull.Value));
        }

        foreach (var q in result.QueueNzbContents)
        {
            Execute(conn, tx, "INSERT INTO QueueNzbContents (Id, NzbContents) VALUES ($Id, $NzbContents)",
                ("$Id", q.Id.ToString()), ("$NzbContents", q.NzbContents));
        }

        foreach (var h in result.HistoryItems)
        {
            Execute(conn, tx, """
                INSERT INTO HistoryItems
                    (Id, CreatedAt, Category, DownloadStatus, DownloadTimeSeconds, FailMessage,
                     FileName, JobName, TotalSegmentBytes, DownloadDirId)
                VALUES
                    ($Id, $CreatedAt, $Category, $DownloadStatus, $DownloadTimeSeconds, $FailMessage,
                     $FileName, $JobName, $TotalSegmentBytes, $DownloadDirId)
                """,
                ("$Id", h.Id.ToString()), ("$CreatedAt", h.CreatedAtUnixSeconds), ("$Category", h.Category),
                ("$DownloadStatus", h.DownloadStatusValue), ("$DownloadTimeSeconds", h.DownloadTimeSeconds),
                ("$FailMessage", (object?)h.FailMessage ?? DBNull.Value), ("$FileName", h.FileName),
                ("$JobName", h.JobName), ("$TotalSegmentBytes", h.TotalSegmentBytes),
                ("$DownloadDirId", (object?)h.DownloadDirId?.ToString() ?? DBNull.Value));
        }

        foreach (var c in result.ConfigItems)
        {
            Execute(conn, tx, """
                INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES ($Name, $Value)
                ON CONFLICT(ConfigName) DO UPDATE SET ConfigValue = excluded.ConfigValue
                """,
                ("$Name", c.ConfigName), ("$Value", c.ConfigValue));
        }

        foreach (var a in result.Accounts)
        {
            Execute(conn, tx, """
                INSERT INTO Accounts (Type, Username, PasswordHash, RandomSalt)
                VALUES ($Type, $Username, $PasswordHash, $RandomSalt)
                """,
                ("$Type", a.Type), ("$Username", a.Username), ("$PasswordHash", a.PasswordHash), ("$RandomSalt", a.RandomSalt));
        }

        foreach (var h in result.HealthCheckResults)
        {
            Execute(conn, tx, """
                INSERT INTO HealthCheckResults (Id, CreatedAt, DavItemId, Path, Result, RepairStatus, Message)
                VALUES ($Id, $CreatedAt, $DavItemId, $Path, $Result, $RepairStatus, $Message)
                """,
                ("$Id", h.Id.ToString()), ("$CreatedAt", h.CreatedAtUnixSeconds), ("$DavItemId", h.DavItemId.ToString()),
                ("$Path", h.Path), ("$Result", h.Result), ("$RepairStatus", h.RepairStatus),
                ("$Message", (object?)h.Message ?? DBNull.Value));
        }

        foreach (var h in result.HealthCheckStats)
        {
            Execute(conn, tx, """
                INSERT INTO HealthCheckStats (DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count)
                VALUES ($Start, $End, $Result, $RepairStatus, $Count)
                """,
                ("$Start", h.DateStartInclusiveUnixSeconds), ("$End", h.DateEndExclusiveUnixSeconds),
                ("$Result", h.Result), ("$RepairStatus", h.RepairStatus), ("$Count", h.Count));
        }

        tx.Commit();
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
