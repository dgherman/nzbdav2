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
    /// <summary>
    /// Round 20: infinidysk's own EF migration 20260820160000_Normalize-Guid-Text-Casing
    /// uppercases every GUID-shaped TEXT column it knows about (see that migration's
    /// GuidTextCasingSql.GuidColumns - the authoritative 18-table/27-column list, read from
    /// infinidysk's source, not guessed) and is already recorded as applied in a freshly-created
    /// target schema BEFORE this tool ever writes a row. Microsoft.Data.Sqlite binds Guid-typed
    /// EF parameters as uppercase hex, and SQLite TEXT comparisons are case-sensitive, so a target
    /// row written with the source database's original (frequently lowercase) casing is invisible
    /// to every later EF Guid-typed lookup - confirmed live: 1,313/1,313 DavMultipartFiles.Id rows
    /// lowercase, breaking both the blobstore migration background job (infinite per-row retry
    /// loop) and legacy-playback fallback (DavDatabaseClient.cs:208) for every affected row.
    /// Guid.ToString() (format "D") already produces exactly the hyphenated lowercase-hex text
    /// infinidysk's own upper(Id) SQL produces the uppercase of, so this is a pure casing
    /// normalization - it does not change which row an Id/FK value refers to.
    /// </summary>
    private static string G(Guid id) => id.ToString().ToUpperInvariant();

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
                ("$Id", G(item.Id)), ("$IdPrefix", item.IdPrefix), ("$CreatedAt", ToSqliteDateTime(item.CreatedAtUnixSeconds)),
                ("$ParentId", (object?)(item.ParentId.HasValue ? G(item.ParentId.Value) : null) ?? DBNull.Value), ("$Name", item.Name),
                ("$FileSize", (object?)item.FileSize ?? DBNull.Value), ("$Type", item.Type), ("$SubType", item.SubType),
                ("$Path", item.Path), ("$ReleaseDate", (object?)item.ReleaseDateUnixSeconds ?? DBNull.Value),
                ("$LastHealthCheck", (object?)item.LastHealthCheckUnixSeconds ?? DBNull.Value),
                ("$NextHealthCheck", (object?)item.NextHealthCheckUnixSeconds ?? DBNull.Value),
                ("$HealthRepairPending", item.HealthRepairPending ? 1 : 0),
                ("$HistoryItemId", (object?)(item.HistoryItemId.HasValue ? G(item.HistoryItemId.Value) : null) ?? DBNull.Value));
        }

        foreach (var f in result.DavNzbFiles)
        {
            Execute(conn, tx, "INSERT INTO DavNzbFiles (Id, SegmentIds) VALUES ($Id, $SegmentIds)",
                ("$Id", G(f.Id)), ("$SegmentIds", f.SegmentIdsJson));
        }

        foreach (var f in result.DavMultipartFiles)
        {
            Execute(conn, tx, "INSERT INTO DavMultipartFiles (Id, Metadata) VALUES ($Id, $Metadata)",
                ("$Id", G(f.Id)), ("$Metadata", f.MetadataJson));
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
                ("$Id", G(q.Id)), ("$CreatedAt", ToSqliteDateTime(q.CreatedAtUnixSeconds)), ("$SortOrder", q.SortOrder),
                ("$FileName", q.FileName), ("$JobName", q.JobName), ("$NzbFileSize", q.NzbFileSize),
                ("$TotalSegmentBytes", q.TotalSegmentBytes), ("$Category", q.Category), ("$Priority", q.Priority),
                ("$PostProcessing", q.PostProcessing),
                ("$PauseUntil", q.PauseUntilUnixSeconds.HasValue ? ToSqliteDateTime(q.PauseUntilUnixSeconds.Value) : DBNull.Value));
        }

        foreach (var q in result.QueueNzbContents)
        {
            Execute(conn, tx, "INSERT INTO QueueNzbContents (Id, NzbContents) VALUES ($Id, $NzbContents)",
                ("$Id", G(q.Id)), ("$NzbContents", q.NzbContents));
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
                ("$Id", G(h.Id)), ("$CreatedAt", ToSqliteDateTime(h.CreatedAtUnixSeconds)), ("$Category", h.Category),
                ("$DownloadStatus", h.DownloadStatusValue), ("$DownloadTimeSeconds", h.DownloadTimeSeconds),
                ("$FailMessage", (object?)h.FailMessage ?? DBNull.Value), ("$FileName", h.FileName),
                ("$JobName", h.JobName), ("$TotalSegmentBytes", h.TotalSegmentBytes),
                ("$DownloadDirId", (object?)(h.DownloadDirId.HasValue ? G(h.DownloadDirId.Value) : null) ?? DBNull.Value));
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
                ("$Id", G(h.Id)), ("$CreatedAt", h.CreatedAtUnixSeconds), ("$DavItemId", G(h.DavItemId)),
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

            // infinidysk's DavNzbFiles/DavRarFiles/DavMultipartFiles all declare a FOREIGN KEY
            // on their Id column referencing DavItems.Id (see infinidysk's
            // DavDatabaseContext.OnModelCreating - HasForeignKey<DavNzbFile>/<DavRarFile>/
            // <DavMultipartFile>(f => f.Id), read-only reference). Microsoft.Data.Sqlite enables
            // `PRAGMA foreign_keys` ON by default (confirmed: a fresh connection reports
            // foreign_keys=1 with no pragma ever set), so SQLite enforces those FKs per-statement
            // unless told otherwise. StreamingMigrator streams DavMultipartFiles/DavRarFiles/
            // DavNzbFiles in one pass before DavItems in a later pass (DavItems' own Type/SubType
            // depends on knowing which multipart/nzb rows were skipped or wrapped first - see
            // StreamingMigrator.Run), so within this single transaction a DavMultipartFiles row
            // can be inserted before its DavItems row exists, which SQLite would otherwise reject
            // immediately with "FOREIGN KEY constraint failed" (reproduced on a real user's
            // --apply run - round-14 fix).
            //
            // `PRAGMA defer_foreign_keys = ON` is SQLite's documented mechanism for exactly this:
            // it defers FK enforcement from per-statement to commit-time, for the CURRENT
            // transaction only (SQLite turns it back off automatically at the end of every
            // transaction, committed or rolled back), so it must be set again for each new
            // transaction rather than once per connection - which is what happens here, since
            // this pragma is set fresh in this constructor every time a TargetWriteSession (and
            // therefore a new transaction) is created. It requires an open transaction to have
            // any effect (deferring only matters while there's something to defer until); setting
            // it immediately after BeginTransaction() above satisfies that.
            using var pragmaCmd = _conn.CreateCommand();
            pragmaCmd.Transaction = _tx;
            pragmaCmd.CommandText = "PRAGMA defer_foreign_keys = ON";
            pragmaCmd.ExecuteNonQuery();
        }

        public void Commit()
        {
            // Round 18: a real --apply run threw "FOREIGN KEY constraint failed" at this
            // Commit() call (the deferred-FK check from round 14 finally running), with rollback
            // clean but the bare SqliteException giving no table/rowid - no way to tell which
            // row was dangling. Checking AFTER a failed deferred-commit is useless: SQLite rolls
            // the whole transaction back the moment the deferred check fails at COMMIT, so by
            // the time a catch block could run a diagnostic query, the offending rows are gone
            // (confirmed empirically: inserted one dangling FK row, forced Commit() to throw,
            // then SELECT COUNT(*) on that table read back 0 - nothing left to inspect). So the
            // check has to run BEFORE Commit(), still inside the pending transaction, where the
            // rows are still visible to `PRAGMA foreign_key_check` (confirmed empirically too:
            // the same setup, queried with the pragma before Commit(), correctly reported
            // table=Child rowid=1 parent=Parent fkid=0 - the exact table/row/referenced-table a
            // bare commit-time exception can't give you).
            var violations = CheckForeignKeyViolations();
            if (violations.Count > 0)
            {
                // Deliberately don't call Commit() at all - throwing here (rather than letting a
                // real commit attempt fail) means Dispose()'s existing dispose-without-commit
                // path handles the rollback exactly the same way every other pre-commit failure
                // in this session already does (see StreamingMigrator's finally block), so this
                // diagnostic can never itself write partial data or change the safe-rollback
                // behavior already in place.
                throw new InvalidOperationException(FormatForeignKeyViolations(violations));
            }

            _tx.Commit();
            _committed = true;
        }

        /// <summary>
        /// Root cause of the round-18 report is still open pending the actual table/rowid this
        /// surfaces on the next real-world run - do NOT guess-fix a specific FK gap from this
        /// alone; this method's only job is enumerating what PRAGMA foreign_key_check reports
        /// while the pending transaction's rows are still visible to it.
        /// </summary>
        private List<ForeignKeyViolation> CheckForeignKeyViolations()
        {
            var violations = new List<ForeignKeyViolation>();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = "PRAGMA foreign_key_check";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var table = reader.GetString(0);
                var rowid = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var parentTable = reader.IsDBNull(2) ? null : reader.GetString(2);
                var fkid = reader.GetInt64(3);
                violations.Add(new ForeignKeyViolation(table, rowid, parentTable, fkid));
            }
            return violations;
        }

        private string FormatForeignKeyViolations(IReadOnlyList<ForeignKeyViolation> violations)
        {
            var lines = new List<string>
            {
                $"Refusing to commit: PRAGMA foreign_key_check found {violations.Count} FOREIGN KEY " +
                "violation(s) in the pending transaction (checked before commit, so these rows are " +
                "still readable - a bare commit-time FOREIGN KEY exception cannot tell you this):",
            };
            foreach (var v in violations)
            {
                // Best-effort: resolve the row's own Id column (every table this tool writes to
                // has one - see SqliteTargetWriter.Apply's INSERT statements) via its SQLite
                // rowid, so the violation names an actual DavItems.Id/etc GUID, not just an
                // opaque internal rowid number. Never lets a resolution failure (e.g. an
                // unexpected schema) hide the underlying violation - falls back to the raw rowid.
                var idDescription = v.RowId.HasValue
                    ? TryResolveRowIdentifier(v.Table, v.RowId.Value) is { } resolved
                        ? $"Id={resolved} (rowid={v.RowId})"
                        : $"rowid={v.RowId}"
                    : "rowid=<unknown>";
                lines.Add(
                    $"  - table \"{v.Table}\" {idDescription} has a foreign key (fkid={v.FkId}) " +
                    $"referencing \"{v.ParentTable ?? "<unknown>"}\" that does not resolve to an " +
                    "existing row there.");
            }
            return string.Join("\n", lines);
        }

        private string? TryResolveRowIdentifier(string table, long rowid)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = _tx;
                cmd.CommandText = $"SELECT Id FROM {QuoteIdentifier(table)} WHERE rowid = $rowid";
                cmd.Parameters.AddWithValue("$rowid", rowid);
                return cmd.ExecuteScalar() as string;
            }
            catch
            {
                // No Id column, or some other lookup failure - the raw rowid in the caller's
                // fallback message is still a real, actionable diagnostic on its own.
                return null;
            }
        }

        // PRAGMA/table-name interpolation above is safe: `table` always comes from
        // PRAGMA foreign_key_check's own result set (SQLite's catalog), never from user input.
        private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private readonly record struct ForeignKeyViolation(string Table, long? RowId, string? ParentTable, long FkId);

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
            SetParam(cmd, "$Id", G(item.Id));
            SetParam(cmd, "$IdPrefix", item.IdPrefix);
            SetParam(cmd, "$CreatedAt", ToSqliteDateTime(item.CreatedAtUnixSeconds));
            SetParam(cmd, "$ParentId", (object?)(item.ParentId.HasValue ? G(item.ParentId.Value) : null) ?? DBNull.Value);
            SetParam(cmd, "$Name", item.Name);
            SetParam(cmd, "$FileSize", (object?)item.FileSize ?? DBNull.Value);
            SetParam(cmd, "$Type", item.Type);
            SetParam(cmd, "$SubType", item.SubType);
            SetParam(cmd, "$Path", item.Path);
            SetParam(cmd, "$ReleaseDate", (object?)item.ReleaseDateUnixSeconds ?? DBNull.Value);
            SetParam(cmd, "$LastHealthCheck", (object?)item.LastHealthCheckUnixSeconds ?? DBNull.Value);
            SetParam(cmd, "$NextHealthCheck", (object?)item.NextHealthCheckUnixSeconds ?? DBNull.Value);
            SetParam(cmd, "$HealthRepairPending", item.HealthRepairPending ? 1 : 0);
            SetParam(cmd, "$HistoryItemId", (object?)(item.HistoryItemId.HasValue ? G(item.HistoryItemId.Value) : null) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public void InsertDavNzbFile(TargetDavNzbFile f)
        {
            var cmd = GetCommand("DavNzbFile", "INSERT INTO DavNzbFiles (Id, SegmentIds) VALUES ($Id, $SegmentIds)");
            SetParam(cmd, "$Id", G(f.Id));
            SetParam(cmd, "$SegmentIds", f.SegmentIdsJson);
            cmd.ExecuteNonQuery();
        }

        public void InsertDavMultipartFile(TargetDavMultipartFile f)
        {
            var cmd = GetCommand("DavMultipartFile", "INSERT INTO DavMultipartFiles (Id, Metadata) VALUES ($Id, $Metadata)");
            SetParam(cmd, "$Id", G(f.Id));
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
            SetParam(cmd, "$Id", G(q.Id));
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
            SetParam(cmd, "$Id", G(q.Id));
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
            SetParam(cmd, "$Id", G(h.Id));
            SetParam(cmd, "$CreatedAt", ToSqliteDateTime(h.CreatedAtUnixSeconds));
            SetParam(cmd, "$Category", h.Category);
            SetParam(cmd, "$DownloadStatus", h.DownloadStatusValue);
            SetParam(cmd, "$DownloadTimeSeconds", h.DownloadTimeSeconds);
            SetParam(cmd, "$FailMessage", (object?)h.FailMessage ?? DBNull.Value);
            SetParam(cmd, "$FileName", h.FileName);
            SetParam(cmd, "$JobName", h.JobName);
            SetParam(cmd, "$TotalSegmentBytes", h.TotalSegmentBytes);
            SetParam(cmd, "$DownloadDirId", (object?)(h.DownloadDirId.HasValue ? G(h.DownloadDirId.Value) : null) ?? DBNull.Value);
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
            SetParam(cmd, "$Id", G(h.Id));
            SetParam(cmd, "$CreatedAt", h.CreatedAtUnixSeconds);
            SetParam(cmd, "$DavItemId", G(h.DavItemId));
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
