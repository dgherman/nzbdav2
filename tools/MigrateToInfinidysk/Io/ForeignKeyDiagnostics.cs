using Microsoft.Data.Sqlite;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Round 18: SQLite's deferred-FK-check-at-commit (`PRAGMA defer_foreign_keys = ON`) auto-rolls-
/// back the whole transaction the instant the check fails at COMMIT, so a bare commit-time
/// SqliteException gives no table/rowid to diagnose from, and by the time a catch block could run
/// a diagnostic query, the offending rows are already gone (confirmed empirically - see the git
/// history on TargetWriteSession.Commit()'s doc comment for the original investigation). The fix
/// is to run `PRAGMA foreign_key_check` BEFORE ever attempting the real commit, still inside the
/// pending transaction, where the rows are still visible to it, and throw a descriptive exception
/// instead of ever calling Commit() at all - letting the caller's own dispose-without-commit path
/// handle rollback exactly like every other pre-commit failure, so this diagnostic can never
/// itself write partial data.
/// <para>
/// Round 22: extracted out of TargetWriteSession.Commit() (where this was first built, for the
/// migration write path) so GuidCasingRepair.Run() (round 20/21's --repair-guid-casing, which
/// commits its own separate transaction) can reuse the exact same check and message format
/// instead of duplicating it - a repair run's casing UPDATEs run under the same deferred-FK
/// regime (round 21) and can just as easily surface a PRE-EXISTING dangling FK reference in the
/// target that has nothing to do with the casing fix itself; both call sites should report it
/// identically.
/// </para>
/// </summary>
internal static class ForeignKeyDiagnostics
{
    internal readonly record struct Violation(string Table, long? RowId, string? ParentTable, long FkId);

    /// <summary>
    /// Runs the check and throws InvalidOperationException with a formatted, table/row/parent-
    /// naming message if anything is found - callers should call this immediately before their
    /// own tx.Commit() and never call Commit() at all if this throws (see class doc comment).
    /// A no-op when there are no violations.
    /// </summary>
    internal static void ThrowIfAnyViolations(SqliteConnection conn, SqliteTransaction tx)
    {
        var violations = Check(conn, tx);
        if (violations.Count > 0)
            throw new InvalidOperationException(Format(conn, tx, violations));
    }

    /// <summary>
    /// Root cause of any individual violation this surfaces is a separate concern - this method's
    /// only job is enumerating what PRAGMA foreign_key_check reports while the pending
    /// transaction's rows are still visible to it.
    /// </summary>
    private static List<Violation> Check(SqliteConnection conn, SqliteTransaction tx)
    {
        var violations = new List<Violation>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA foreign_key_check";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            var rowid = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var parentTable = reader.IsDBNull(2) ? null : reader.GetString(2);
            var fkid = reader.GetInt64(3);
            violations.Add(new Violation(table, rowid, parentTable, fkid));
        }
        return violations;
    }

    private static string Format(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<Violation> violations)
    {
        var lines = new List<string>
        {
            $"Refusing to commit: PRAGMA foreign_key_check found {violations.Count} FOREIGN KEY " +
            "violation(s) in the pending transaction (checked before commit, so these rows are " +
            "still readable - a bare commit-time FOREIGN KEY exception cannot tell you this):",
        };
        foreach (var v in violations)
        {
            // Best-effort: resolve the row's own Id column (every table either write path in this
            // tool touches has one) via its SQLite rowid, so the violation names an actual
            // DavItems.Id/etc GUID, not just an opaque internal rowid number. Never lets a
            // resolution failure (e.g. an unexpected schema) hide the underlying violation -
            // falls back to the raw rowid.
            var idDescription = v.RowId.HasValue
                ? TryResolveRowIdentifier(conn, tx, v.Table, v.RowId.Value) is { } resolved
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

    private static string? TryResolveRowIdentifier(SqliteConnection conn, SqliteTransaction tx, string table, long rowid)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
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
}
