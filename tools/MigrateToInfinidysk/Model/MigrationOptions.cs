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
        Archive: new ArchivePayload([], [], [], [], [], [], [], [], [], [], [], []));
}

/// <summary>
/// Sidecar JSON archive content for data that has no destination in infinidysk: rows from
/// wholly-incompatible tables, HistoryItem fields infinidysk doesn't have a column for,
/// HealthCheckResult.Operation (infinidysk drops this field), skipped ConfigItems keys,
/// skipped obfuscated-content files (full recoverable payload, not just a message - see
/// ArchivedObfuscatedFile), and DavNzbFiles.SegmentFallbacks (infinidysk's DavNzbFiles table
/// has no SQL column for these - see ArchivedNzbFileFallback).
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
    IReadOnlyList<ArchivedObfuscatedFile> SkippedObfuscatedFiles,
    IReadOnlyList<ArchivedNzbFileFallback> DavNzbFileFallbackIds);

public record ArchivedHistoryItemFields(
    Guid HistoryItemId, bool IsHidden, long? HiddenAtUnixSeconds, string? NzbContents,
    string? FailureReason, bool IsImported, bool IsArchived, long? ArchivedAtUnixSeconds);

public record ArchivedHealthCheckOperation(Guid HealthCheckResultId, string Operation);

/// <summary>
/// Full recoverable payload for a DavMultipartFiles/DavRarFiles row that couldn't be migrated
/// because of an obfuscation key infinidysk has no field for (see ObfuscationDetector). Carries
/// everything a human would need to manually re-process the file, not just a diagnostic string:
/// the DavItem this file belonged to (also excluded from the migrated DavItems - see
/// Migrator.Run), the key itself when one was recorded, and the full source metadata JSON
/// (segment IDs, byte ranges, AES params if any).
/// </summary>
public record ArchivedObfuscatedFile(
    Guid DavItemId, string Reason, string? ObfuscationKeyBase64, string SourceMetadataJson);

/// <summary>
/// DavNzbFiles.SegmentFallbacks (alternate message IDs for segments with duplicate segment
/// numbers in the NZB - used as a retry path when the primary article is missing). infinidysk's
/// DavNzbFiles table has only an Id and SegmentIds column; there is no SQL column to carry this
/// data into the live target database, so it's preserved here instead of being silently dropped.
/// </summary>
public record ArchivedNzbFileFallback(Guid DavNzbFileId, string[] SegmentIds, string[][] SegmentFallbackIds);
