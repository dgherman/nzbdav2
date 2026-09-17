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
}
