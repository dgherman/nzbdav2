using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Refuses to run against a source older than the shared ancestor migration, or a target that
/// isn't already fully migrated to infinidysk's current schema. Both checks read the
/// __EFMigrationsHistory table (EF Core's own bookkeeping - never written to by this tool).
/// </summary>
public static class SchemaGuard
{
    // Last migration nzbdav2 and infinidysk still shared before their histories diverged.
    public const string SharedAncestorMigration = "20251113081523_Populate-Usenet-Providers-Config";

    // The mapping rules this tool implements depend specifically on these infinidysk
    // migrations having already run against the target database.
    public static readonly IReadOnlyList<string> RequiredTargetMigrations =
    [
        "20260129182923_Update-DavItems-Type-And-SubType",
        "20260731171110_Add-SingleAdmin-UniqueIndex",
        "20260817160000_Add-QueueItem-SortOrder",
    ];

    public readonly record struct Result(bool IsValid, string? ErrorMessage)
    {
        public static Result Valid() => new(true, null);
        public static Result Invalid(string message) => new(false, message);
    }

    public static Result CheckSource(IReadOnlyCollection<string> sourceMigrationIds)
    {
        if (!sourceMigrationIds.Contains(SharedAncestorMigration))
        {
            return Result.Invalid(
                $"Source database has not run migration '{SharedAncestorMigration}' (the last migration " +
                "nzbdav2 and infinidysk shared). This tool only supports migrating from a nzbdav2 database " +
                "at or after that point - start nzbdav2, let it finish migrating, then re-run this tool.");
        }

        return Result.Valid();
    }

    public static Result CheckTarget(IReadOnlyCollection<string> targetMigrationIds)
    {
        var missing = RequiredTargetMigrations.Where(m => !targetMigrationIds.Contains(m)).ToList();
        if (missing.Count > 0)
        {
            return Result.Invalid(
                "Target database is not at infinidysk's fully-migrated schema. Missing migrations: " +
                string.Join(", ", missing) + ". Start infinidysk once against this config volume and let it " +
                "finish its own startup migration before running this tool with --apply.");
        }

        return Result.Valid();
    }

    // Every column SqliteTargetWriter's INSERT statements actually touch, plus DavItems.FileBlobId
    // (written by nothing here, but its presence is required proof the target really is on
    // infinidysk's current schema - see UsenetFileToBlobstoreMigrationService, which is what
    // lazily converts the legacy rows this tool writes). Checking __EFMigrationsHistory alone
    // (CheckTarget above) is a cheap pre-check, not authoritative: a hand-built or tampered
    // fixture/database can have matching migration rows without the columns actually existing.
    // This is the check that decides whether --apply's INSERTs will actually succeed.
    private static readonly IReadOnlyDictionary<string, string[]> RequiredTargetColumns = new Dictionary<string, string[]>
    {
        ["DavItems"] =
        [
            "Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "FileSize", "Type", "SubType", "Path",
            "ReleaseDate", "LastHealthCheck", "NextHealthCheck", "HealthRepairPending", "FileBlobId", "NzbBlobId", "HistoryItemId",
        ],
        ["DavNzbFiles"] = ["Id", "SegmentIds"],
        ["DavMultipartFiles"] = ["Id", "Metadata"],
        ["QueueItems"] =
        [
            "Id", "CreatedAt", "SortOrder", "FileName", "JobName", "NzbFileSize", "TotalSegmentBytes",
            "Category", "Priority", "PostProcessing", "PauseUntil",
        ],
        ["QueueNzbContents"] = ["Id", "NzbContents"],
        ["HistoryItems"] =
        [
            "Id", "CreatedAt", "Category", "DownloadStatus", "DownloadTimeSeconds", "FailMessage",
            "FileName", "JobName", "TotalSegmentBytes", "DownloadDirId",
        ],
        ["ConfigItems"] = ["ConfigName", "ConfigValue"],
        ["Accounts"] = ["Type", "Username", "PasswordHash", "RandomSalt"],
        ["HealthCheckResults"] = ["Id", "CreatedAt", "DavItemId", "Path", "Result", "RepairStatus", "Message"],
        ["HealthCheckStats"] = ["DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count"],
    };

    public static Result CheckTargetSchema(SqliteConnection conn)
    {
        var problems = new List<string>();

        foreach (var (table, columns) in RequiredTargetColumns)
        {
            var actualColumns = ReadColumnNames(conn, table);
            if (actualColumns == null)
            {
                problems.Add($"table '{table}' is missing entirely");
                continue;
            }

            var missingColumns = columns.Where(c => !actualColumns.Contains(c)).ToList();
            if (missingColumns.Count > 0)
                problems.Add($"table '{table}' is missing column(s): {string.Join(", ", missingColumns)}");
        }

        // The multi-admin conflict check (AdminSelector) relies on infinidysk's own
        // IX_Accounts_SingleAdmin unique filtered index actually being present and enforced by
        // the target DB - without it, a race or a bug in this tool could write two admin rows
        // with nothing to stop it. Verify not just that an index by that name exists and is
        // UNIQUE, but that it indexes the right column and carries the right partial-index
        // predicate - a same-named unique index on the wrong column, or without the WHERE
        // Type = 1 filter (so it'd also collide across WebDav accounts), gives no real
        // protection even though the name/uniqueness check alone would pass it.
        var indexProblem = ValidateSingleAdminUniqueIndex(conn);
        if (indexProblem != null)
            problems.Add(indexProblem);

        if (problems.Count > 0)
        {
            return Result.Invalid(
                "Target database does not have infinidysk's full current schema - refusing to write " +
                "anything. Problems found:\n  - " + string.Join("\n  - ", problems) +
                "\nStart infinidysk once against this config volume and let it finish its own startup " +
                "migration before running this tool with --apply.");
        }

        return Result.Valid();
    }

    private const string SingleAdminIndexName = "IX_Accounts_SingleAdmin";

    /// <summary>
    /// Returns null when a valid IX_Accounts_SingleAdmin index is present (correct name,
    /// UNIQUE, indexes exactly the Type column, and carries a partial-index predicate that
    /// means "Type = 1 (Admin)"), or a problem description otherwise.
    /// </summary>
    private static string? ValidateSingleAdminUniqueIndex(SqliteConnection conn)
    {
        if (!TryGetIndexUniqueness(conn, SingleAdminIndexName, out var isUnique))
            return $"Accounts table is missing the '{SingleAdminIndexName}' unique index";

        if (!isUnique)
            return $"Accounts.{SingleAdminIndexName} exists but is not a UNIQUE index";

        var indexedColumns = GetIndexedColumns(conn, SingleAdminIndexName);
        if (indexedColumns is not ["Type"])
        {
            return $"Accounts.{SingleAdminIndexName} exists but indexes column(s) " +
                   $"[{string.Join(", ", indexedColumns)}] instead of Type";
        }

        var createSql = GetIndexCreateSql(conn, SingleAdminIndexName);
        if (createSql == null || !HasAdminOnlyPredicate(createSql))
        {
            return $"Accounts.{SingleAdminIndexName} exists on the right column but its partial-index " +
                   "predicate doesn't restrict it to Type = 1 (Admin) - as defined, it wouldn't stop " +
                   "multiple admin accounts, or would incorrectly restrict other account types too";
        }

        return null;
    }

    // Account.AccountType.Admin = 1 (backend/Database/Models/Account.cs, both projects).
    //
    // Round 4 used Regex.IsMatch (a substring search) against the WHERE clause text, which a
    // crafted predicate like `Type = 1 AND 0` satisfies while indexing zero rows - the extra
    // `AND 0` is never checked for, so anything containing the right substring passes regardless
    // of what else is in the expression. Fixed properly rather than patching that one instance:
    // the extracted predicate must now EXACT-MATCH the single canonical expression after
    // normalizing only identifier quoting (`"Type"`/`[Type]`/`` `Type` `` -> `Type`, which
    // SQLite may round-trip differently than what infinidysk's migration literally wrote) and
    // whitespace (collapsed, and spacing forced around `=` so `Type=1` and `Type = 1` compare
    // equal) - no other leniency. Any extra token, condition, operator, or reordering makes the
    // normalized string differ from the canonical one and is rejected, closing the whole class
    // of "predicate contains the right substring plus something else" bypasses, not just the
    // one reported.
    private const string CanonicalAdminOnlyPredicate = "Type = 1";

    private static bool HasAdminOnlyPredicate(string createIndexSql)
    {
        var whereIndex = createIndexSql.IndexOf("where", StringComparison.OrdinalIgnoreCase);
        if (whereIndex < 0)
            return false; // not a partial index at all

        var rawPredicate = createIndexSql[(whereIndex + "where".Length)..];
        var normalized = NormalizePredicate(rawPredicate);
        return string.Equals(normalized, CanonicalAdminOnlyPredicate, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePredicate(string predicate)
    {
        // Strip identifier-quoting characters only. There are no string literals in this
        // predicate (the only literal is the bare numeral 1), so this can't accidentally eat
        // part of a value the way it would if the predicate could contain quoted strings.
        var noQuotes = predicate.Replace("\"", "").Replace("'", "").Replace("[", "")
            .Replace("]", "").Replace("`", "");

        // Force consistent spacing around '=' so `Type=1` and `Type = 1` normalize identically,
        // then collapse all remaining whitespace runs (including newlines) to a single space.
        var spacedEquals = Regex.Replace(noQuotes, @"\s*=\s*", " = ");
        return Regex.Replace(spacedEquals, @"\s+", " ").Trim();
    }

    private static bool TryGetIndexUniqueness(SqliteConnection conn, string indexName, out bool isUnique)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_list(\"Accounts\")";
        using var reader = cmd.ExecuteReader();
        var nameOrdinal = -1;
        var uniqueOrdinal = -1;
        while (reader.Read())
        {
            if (nameOrdinal < 0) nameOrdinal = reader.GetOrdinal("name");
            if (uniqueOrdinal < 0) uniqueOrdinal = reader.GetOrdinal("unique");

            if (string.Equals(reader.GetString(nameOrdinal), indexName, StringComparison.Ordinal))
            {
                isUnique = reader.GetInt64(uniqueOrdinal) != 0;
                return true;
            }
        }
        isUnique = false;
        return false;
    }

    private static IReadOnlyList<string> GetIndexedColumns(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_info(" + QuoteIdentifier(indexName) + ")";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        var nameOrdinal = -1;
        while (reader.Read())
        {
            if (nameOrdinal < 0) nameOrdinal = reader.GetOrdinal("name");
            columns.Add(reader.GetString(nameOrdinal));
        }
        return columns;
    }

    private static string? GetIndexCreateSql(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        cmd.Parameters.AddWithValue("$name", indexName);
        return cmd.ExecuteScalar() as string;
    }

    private static HashSet<string>? ReadColumnNames(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(" + QuoteIdentifier(table) + ")";
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        return columns.Count == 0 ? null : columns;
    }

    // PRAGMA statements don't accept bound parameters; the table names here come only from our
    // own fixed RequiredTargetColumns dictionary above, never from user input.
    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
