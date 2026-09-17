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
        // with nothing to stop it. Verify the index exists by name (matching infinidysk's
        // Add-SingleAdmin-UniqueIndex migration) and is actually UNIQUE.
        if (!HasSingleAdminUniqueIndex(conn))
            problems.Add("Accounts table is missing the 'IX_Accounts_SingleAdmin' unique index");

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

    private static bool HasSingleAdminUniqueIndex(SqliteConnection conn)
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

            var name = reader.GetString(nameOrdinal);
            var isUnique = reader.GetInt64(uniqueOrdinal) != 0;
            if (isUnique && string.Equals(name, "IX_Accounts_SingleAdmin", StringComparison.Ordinal))
                return true;
        }
        return false;
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
