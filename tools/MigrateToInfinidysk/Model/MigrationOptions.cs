namespace NzbWebDAV.MigrateToInfinidysk.Model;

public record MigrationOptions(string? RequestedAdminUsername);

/// <summary>Per-table row counts, for the dry-run report and end-of-run summary.</summary>
public record TableCounts(int Copied, int Skipped, int Archived);

public record MigrationResult(
    bool Success,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, TableCounts> Counts,
    IReadOnlyList<TargetDavItem> DavItems,
    IReadOnlyList<TargetDavNzbFile> DavNzbFiles,
    IReadOnlyList<TargetDavMultipartFile> DavMultipartFiles,
    IReadOnlyList<TargetQueueItem> QueueItems,
    IReadOnlyList<TargetQueueNzbContents> QueueNzbContents,
    IReadOnlyList<TargetHistoryItem> HistoryItems,
    IReadOnlyList<TargetConfigItem> ConfigItems,
    IReadOnlyList<TargetAccount> Accounts,
    IReadOnlyList<TargetHealthCheckResult> HealthCheckResults,
    IReadOnlyList<TargetHealthCheckStat> HealthCheckStats,
    ArchivePayload Archive)
{
    public static MigrationResult Failed(params string[] errors) => new(
        Success: false, Errors: errors, Warnings: [], Counts: new Dictionary<string, TableCounts>(),
        DavItems: [], DavNzbFiles: [], DavMultipartFiles: [], QueueItems: [], QueueNzbContents: [],
        HistoryItems: [], ConfigItems: [], Accounts: [], HealthCheckResults: [], HealthCheckStats: [],
        Archive: new ArchivePayload([], [], [], [], [], [], [], [], [], [], []));
}

/// <summary>
/// Sidecar JSON archive content for data that has no destination in infinidysk: rows from
/// wholly-incompatible tables, HistoryItem fields infinidysk doesn't have a column for,
/// HealthCheckResult.Operation (infinidysk drops this field), skipped ConfigItems keys, and
/// skipped obfuscation-key rows.
/// </summary>
public record ArchivePayload(
    IReadOnlyList<IncompatibleTableRow> LocalLinks,
    IReadOnlyList<IncompatibleTableRow> AnalysisHistoryItems,
    IReadOnlyList<IncompatibleTableRow> BandwidthSamples,
    IReadOnlyList<IncompatibleTableRow> MissingArticleEvents,
    IReadOnlyList<IncompatibleTableRow> MissingArticleSummaries,
    IReadOnlyList<IncompatibleTableRow> NzbProviderStats,
    IReadOnlyList<IncompatibleTableRow> ProviderBenchmarkResults,
    IReadOnlyList<ArchivedHistoryItemFields> HistoryItemDroppedFields,
    IReadOnlyList<ArchivedHealthCheckOperation> HealthCheckDroppedOperations,
    IReadOnlyList<(string ConfigName, string ConfigValue)> SkippedConfigItems,
    IReadOnlyList<string> SkippedObfuscatedRows);

public record ArchivedHistoryItemFields(
    Guid HistoryItemId, bool IsHidden, long? HiddenAtUnixSeconds, string? NzbContents,
    string? FailureReason, bool IsImported, bool IsArchived, long? ArchivedAtUnixSeconds);

public record ArchivedHealthCheckOperation(Guid HealthCheckResultId, string Operation);
