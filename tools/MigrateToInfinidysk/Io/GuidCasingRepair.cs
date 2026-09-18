using Microsoft.Data.Sqlite;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Round 20: repairs a target infinidysk db.sqlite that this tool already wrote GUID-shaped TEXT
/// columns to with the source nzbdav2 database's original casing (frequently lowercase). Those
/// rows are invisible to EF's Guid-typed parameterized queries (Microsoft.Data.Sqlite binds Guid
/// parameters uppercase; SQLite TEXT comparisons are case-sensitive) - confirmed live: 1,313/
/// 1,313 DavMultipartFiles.Id rows lowercase, breaking the blobstore migration background job and
/// legacy-playback fallback for every affected row. See PART 1's fix in SqliteTargetWriter.cs for
/// the tool-side fix that prevents this on future migrations - this class is the repair path for
/// data this tool already wrote to a live target before that fix existed.
/// <para>
/// The table/column list below is copied verbatim from infinidysk's own EF migration
/// 20260820160000_Normalize-Guid-Text-Casing (GuidTextCasingSql.GuidColumns in
/// ~/Documents/projects/infinidysk/backend/Database/MigrationHelpers/GuidTextCasingSql.cs, a
/// read-only reference repo - 18 tables / 27 columns, "the SQLite DavDatabaseContext snapshot at
/// the Normalize-Guid-Text-Casing migration"), the authoritative source for which columns
/// infinidysk itself considers GUID-shaped TEXT. Do not guess a subset.
/// </para>
/// </summary>
public static class GuidCasingRepair
{
    internal static readonly (string Table, string[] Columns)[] GuidColumns =
    [
        ("BlobCleanupItems", ["Id"]),
        ("DavCleanupItems", ["Id"]),
        ("DavItems", ["Id", "ParentId", "HistoryItemId", "FileBlobId", "NzbBlobId"]),
        ("DavMultipartFiles", ["Id"]),
        ("DavNzbFiles", ["Id"]),
        ("DavRarFiles", ["Id"]),
        ("HealthCheckResults", ["Id", "DavItemId"]),
        ("HistoryCleanupItems", ["Id"]),
        ("HistoryItems", ["Id", "DownloadDirId", "NzbBlobId"]),
        ("ListSources", ["Id"]),
        ("NzbBlobCleanupItems", ["Id"]),
        ("NzbNames", ["Id"]),
        ("NzbResolutionGroups", ["Id"]),
        ("Par2RepairJobs", ["Id", "DavItemId"]),
        ("QueueItems", ["Id"]),
        ("QueueNzbContents", ["Id"]),
        ("WantedItems", ["Id"]),
        ("WatchdogEntries", ["ClickId", "QueueItemId"]),
    ];

    public record ColumnRepair(string Table, string Column, int RowsChanged);

    public record RepairReport(IReadOnlyList<ColumnRepair> Changes)
    {
        public int TotalRowsChanged => Changes.Sum(c => c.RowsChanged);
    }

    /// <summary>
    /// Idempotent and safe: only ever rewrites a column's existing value to its own uppercase
    /// form (never touches which row a value refers to), only mutates values that are not
    /// already uppercase (so a second run finds nothing left to change - see the WHERE clause in
    /// RepairColumn), skips any table from the authoritative list that doesn't exist in this
    /// target DB (older/smaller test fixtures, or a schema variant that never got that table),
    /// and verifies each touched table's row COUNT(*) is unchanged before/after (this only ever
    /// runs UPDATE, never INSERT/DELETE, but the check catches a coding mistake here rather than
    /// trusting that by construction). One transaction for the whole repair - a failure partway
    /// through leaves the target completely untouched, same atomicity guarantee as every other
    /// write path in this tool.
    /// </summary>
    public static RepairReport Run(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        var changes = new List<ColumnRepair>();

        foreach (var (table, columns) in GuidColumns)
        {
            if (!TableExists(conn, tx, table)) continue;

            var countBefore = CountRows(conn, tx, table);

            foreach (var column in columns)
            {
                // Column-existence check, not just table-existence: a real, fully-migrated
                // target DB has every column in the authoritative list (the migration this list
                // came from ran against exactly that schema), but a smaller/older test fixture
                // (or a schema variant) might have the table without every column - skip rather
                // than throw, same "safe on whatever's actually there" spirit as the table check.
                if (!ColumnExists(conn, tx, table, column)) continue;

                var rowsChanged = RepairColumn(conn, tx, table, column);
                if (rowsChanged > 0)
                    changes.Add(new ColumnRepair(table, column, rowsChanged));
            }

            var countAfter = CountRows(conn, tx, table);
            if (countBefore != countAfter)
            {
                throw new InvalidOperationException(
                    $"GUID casing repair changed {table}'s row count ({countBefore} -> {countAfter}) - " +
                    "this should only ever run UPDATE, never INSERT/DELETE. Refusing to commit.");
            }
        }

        tx.Commit();
        return new RepairReport(changes);
    }

    private static int RepairColumn(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Only rows whose value isn't already uppercase are touched - this is what makes a
        // second run a no-op: after the first run, every value already equals upper(value), so
        // this WHERE clause matches nothing.
        cmd.CommandText =
            $"UPDATE {Quote(table)} SET {Quote(column)} = upper({Quote(column)}) " +
            $"WHERE {Quote(column)} IS NOT NULL AND {Quote(column)} <> upper({Quote(column)})";
        return cmd.ExecuteNonQuery();
    }

    private static int CountRows(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $name";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$name", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // Table/column names above always come from the fixed, hardcoded GuidColumns list, never
    // from external input - this quoting exists for SQL correctness (reserved-word safety), not
    // as an injection defense.
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
