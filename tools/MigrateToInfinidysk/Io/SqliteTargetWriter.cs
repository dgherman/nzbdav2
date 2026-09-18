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
                ("$Id", item.Id.ToString()), ("$IdPrefix", item.IdPrefix), ("$CreatedAt", ToSqliteDateTime(item.CreatedAtUnixSeconds)),
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
                ("$Id", q.Id.ToString()), ("$CreatedAt", ToSqliteDateTime(q.CreatedAtUnixSeconds)), ("$SortOrder", q.SortOrder),
                ("$FileName", q.FileName), ("$JobName", q.JobName), ("$NzbFileSize", q.NzbFileSize),
                ("$TotalSegmentBytes", q.TotalSegmentBytes), ("$Category", q.Category), ("$Priority", q.Priority),
                ("$PostProcessing", q.PostProcessing),
                ("$PauseUntil", q.PauseUntilUnixSeconds.HasValue ? ToSqliteDateTime(q.PauseUntilUnixSeconds.Value) : DBNull.Value));
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
                ("$Id", h.Id.ToString()), ("$CreatedAt", ToSqliteDateTime(h.CreatedAtUnixSeconds)), ("$Category", h.Category),
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

    /// <summary>
    /// DavItems.CreatedAt, QueueItems.CreatedAt/PauseUntil, and HistoryItems.CreatedAt have no
    /// HasConversion in infinidysk's DbContext, so EF's Sqlite provider stores them as plain
    /// DateTime-formatted TEXT (not Unix-seconds INTEGER, unlike ReleaseDate/LastHealthCheck/
    /// NextHealthCheck and the HealthCheck* tables, which infinidysk DOES convert to Unix
    /// seconds explicitly - those stay as raw longs below). Binding a real DateTime through
    /// SqliteParameter (rather than hand-formatting a string) lets Microsoft.Data.Sqlite apply
    /// its own native TEXT representation, matching what EF Core's reads/writes expect.
    /// </summary>
    private static DateTime ToSqliteDateTime(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Streaming counterpart to <see cref="Apply"/>: instead of taking a fully-materialized
    /// MigrationResult and writing every row inside one method call, opens a transaction up front
    /// and exposes one Insert method per table that a caller (StreamingMigrator) calls row by
    /// row as it streams the source database, without needing to hold a full list of target rows
    /// for any table. Each Insert method reuses one prepared SqliteCommand across every row for
    /// that table (rather than the one-command-per-row pattern <see cref="Execute"/> above uses)
    /// so a table with thousands of rows doesn't also allocate thousands of SqliteCommand/
    /// SqliteParameter objects. Same INSERT statements as <see cref="Apply"/>, same "one
    /// transaction, commit only at the very end" atomicity - see MigrationApplier for why the
    /// commit happens only after the archive is durably published.
    /// </summary>
    public sealed class TargetWriteSession : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;
        private readonly Dictionary<string, SqliteCommand> _commands = new();
        private bool _committed;

        public TargetWriteSession(SqliteConnection conn)
        {
            _conn = conn;
            _tx = conn.BeginTransaction();
        }

        public void Commit()
        {
            _tx.Commit();
            _committed = true;
        }

        public void Dispose()
        {
            foreach (var cmd in _commands.Values) cmd.Dispose();
            if (!_committed) _tx.Dispose(); // dispose-without-commit = rollback
            else _tx.Dispose();
        }

        private SqliteCommand GetCommand(string key, string sql)
        {
            if (_commands.TryGetValue(key, out var existing)) return existing;
            var cmd = _conn.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = sql;
            _commands[key] = cmd;
            return cmd;
        }

        private static void SetParam(SqliteCommand cmd, string name, object value)
        {
            if (cmd.Parameters.Contains(name)) cmd.Parameters[name].Value = value;
            else cmd.Parameters.AddWithValue(name, value);
        }

        public void InsertDavItem(TargetDavItem item)
        {
            var cmd = GetCommand("DavItem", """
                INSERT INTO DavItems
                    (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path,
                     ReleaseDate, LastHealthCheck, NextHealthCheck, HealthRepairPending, HistoryItemId)
                VALUES
                    ($Id, $IdPrefix, $CreatedAt, $ParentId, $Name, $FileSize, $Type, $SubType, $Path,
                     $ReleaseDate, $LastHealthCheck, $NextHealthCheck, $HealthRepairPending, $HistoryItemId)
                ON CONFLICT(Id) DO UPDATE SET
                    Type = excluded.Type, SubType = excluded.SubType
                """);
            SetParam(cmd, "$Id", item.Id.ToString());
            SetParam(cmd, "$IdPrefix", item.IdPrefix);
            SetParam(cmd, "$CreatedAt", ToSqliteDateTime(item.CreatedAtUnixSeconds));
            SetParam(cmd, "$ParentId", (object?)item.ParentId?.ToString() ?? DBNull.Value);
            SetParam(cmd, "$Name", item.Name);
            SetParam(cmd, "$FileSize", (object?)item.FileSize ?? DBNull.Value);
            SetParam(cmd, "$Type", item.Type);
            SetParam(cmd, "$SubType", item.SubType);
            SetParam(cmd, "$Path", item.Path);
            SetParam(cmd, "$ReleaseDate", (object?)item.ReleaseDateUnixSeconds ?? DBNull.Value);
            SetParam(cmd, "$LastHealthCheck", (object?)item.LastHealthCheckUnixSeconds ?? DBNull.Value);
            SetParam(cmd, "$NextHealthCheck", (object?)item.NextHealthCheckUnixSeconds ?? DBNull.Value);
            SetParam(cmd, "$HealthRepairPending", item.HealthRepairPending ? 1 : 0);
            SetParam(cmd, "$HistoryItemId", (object?)item.HistoryItemId?.ToString() ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public void InsertDavNzbFile(TargetDavNzbFile f)
        {
            var cmd = GetCommand("DavNzbFile", "INSERT INTO DavNzbFiles (Id, SegmentIds) VALUES ($Id, $SegmentIds)");
            SetParam(cmd, "$Id", f.Id.ToString());
            SetParam(cmd, "$SegmentIds", f.SegmentIdsJson);
            cmd.ExecuteNonQuery();
        }

        public void InsertDavMultipartFile(TargetDavMultipartFile f)
        {
            var cmd = GetCommand("DavMultipartFile", "INSERT INTO DavMultipartFiles (Id, Metadata) VALUES ($Id, $Metadata)");
            SetParam(cmd, "$Id", f.Id.ToString());
            SetParam(cmd, "$Metadata", f.MetadataJson);
            cmd.ExecuteNonQuery();
        }

        public void InsertQueueItem(TargetQueueItem q)
        {
            var cmd = GetCommand("QueueItem", """
                INSERT INTO QueueItems
                    (Id, CreatedAt, SortOrder, FileName, JobName, NzbFileSize, TotalSegmentBytes,
                     Category, Priority, PostProcessing, PauseUntil)
                VALUES
                    ($Id, $CreatedAt, $SortOrder, $FileName, $JobName, $NzbFileSize, $TotalSegmentBytes,
                     $Category, $Priority, $PostProcessing, $PauseUntil)
                """);
            SetParam(cmd, "$Id", q.Id.ToString());
            SetParam(cmd, "$CreatedAt", ToSqliteDateTime(q.CreatedAtUnixSeconds));
            SetParam(cmd, "$SortOrder", q.SortOrder);
            SetParam(cmd, "$FileName", q.FileName);
            SetParam(cmd, "$JobName", q.JobName);
            SetParam(cmd, "$NzbFileSize", q.NzbFileSize);
            SetParam(cmd, "$TotalSegmentBytes", q.TotalSegmentBytes);
            SetParam(cmd, "$Category", q.Category);
            SetParam(cmd, "$Priority", q.Priority);
            SetParam(cmd, "$PostProcessing", q.PostProcessing);
            SetParam(cmd, "$PauseUntil", q.PauseUntilUnixSeconds.HasValue ? ToSqliteDateTime(q.PauseUntilUnixSeconds.Value) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public void InsertQueueNzbContents(TargetQueueNzbContents q)
        {
            var cmd = GetCommand("QueueNzbContents", "INSERT INTO QueueNzbContents (Id, NzbContents) VALUES ($Id, $NzbContents)");
            SetParam(cmd, "$Id", q.Id.ToString());
            SetParam(cmd, "$NzbContents", q.NzbContents);
            cmd.ExecuteNonQuery();
        }

        public void InsertHistoryItem(TargetHistoryItem h)
        {
            var cmd = GetCommand("HistoryItem", """
                INSERT INTO HistoryItems
                    (Id, CreatedAt, Category, DownloadStatus, DownloadTimeSeconds, FailMessage,
                     FileName, JobName, TotalSegmentBytes, DownloadDirId)
                VALUES
                    ($Id, $CreatedAt, $Category, $DownloadStatus, $DownloadTimeSeconds, $FailMessage,
                     $FileName, $JobName, $TotalSegmentBytes, $DownloadDirId)
                """);
            SetParam(cmd, "$Id", h.Id.ToString());
            SetParam(cmd, "$CreatedAt", ToSqliteDateTime(h.CreatedAtUnixSeconds));
            SetParam(cmd, "$Category", h.Category);
            SetParam(cmd, "$DownloadStatus", h.DownloadStatusValue);
            SetParam(cmd, "$DownloadTimeSeconds", h.DownloadTimeSeconds);
            SetParam(cmd, "$FailMessage", (object?)h.FailMessage ?? DBNull.Value);
            SetParam(cmd, "$FileName", h.FileName);
            SetParam(cmd, "$JobName", h.JobName);
            SetParam(cmd, "$TotalSegmentBytes", h.TotalSegmentBytes);
            SetParam(cmd, "$DownloadDirId", (object?)h.DownloadDirId?.ToString() ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public void InsertConfigItem(TargetConfigItem c)
        {
            var cmd = GetCommand("ConfigItem", """
                INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES ($Name, $Value)
                ON CONFLICT(ConfigName) DO UPDATE SET ConfigValue = excluded.ConfigValue
                """);
            SetParam(cmd, "$Name", c.ConfigName);
            SetParam(cmd, "$Value", c.ConfigValue);
            cmd.ExecuteNonQuery();
        }

        public void InsertAccount(TargetAccount a)
        {
            var cmd = GetCommand("Account", """
                INSERT INTO Accounts (Type, Username, PasswordHash, RandomSalt)
                VALUES ($Type, $Username, $PasswordHash, $RandomSalt)
                """);
            SetParam(cmd, "$Type", a.Type);
            SetParam(cmd, "$Username", a.Username);
            SetParam(cmd, "$PasswordHash", a.PasswordHash);
            SetParam(cmd, "$RandomSalt", a.RandomSalt);
            cmd.ExecuteNonQuery();
        }

        public void InsertHealthCheckResult(TargetHealthCheckResult h)
        {
            var cmd = GetCommand("HealthCheckResult", """
                INSERT INTO HealthCheckResults (Id, CreatedAt, DavItemId, Path, Result, RepairStatus, Message)
                VALUES ($Id, $CreatedAt, $DavItemId, $Path, $Result, $RepairStatus, $Message)
                """);
            SetParam(cmd, "$Id", h.Id.ToString());
            SetParam(cmd, "$CreatedAt", h.CreatedAtUnixSeconds);
            SetParam(cmd, "$DavItemId", h.DavItemId.ToString());
            SetParam(cmd, "$Path", h.Path);
            SetParam(cmd, "$Result", h.Result);
            SetParam(cmd, "$RepairStatus", h.RepairStatus);
            SetParam(cmd, "$Message", (object?)h.Message ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public void InsertHealthCheckStat(TargetHealthCheckStat h)
        {
            var cmd = GetCommand("HealthCheckStat", """
                INSERT INTO HealthCheckStats (DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count)
                VALUES ($Start, $End, $Result, $RepairStatus, $Count)
                """);
            SetParam(cmd, "$Start", h.DateStartInclusiveUnixSeconds);
            SetParam(cmd, "$End", h.DateEndExclusiveUnixSeconds);
            SetParam(cmd, "$Result", h.Result);
            SetParam(cmd, "$RepairStatus", h.RepairStatus);
            SetParam(cmd, "$Count", h.Count);
            cmd.ExecuteNonQuery();
        }
    }
}
