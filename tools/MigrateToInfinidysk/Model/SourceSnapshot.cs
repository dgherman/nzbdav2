namespace NzbWebDAV.MigrateToInfinidysk.Model;

/// <summary>
/// Everything read out of one nzbdav2 db.sqlite. Read-only - Migrator.Run never mutates this.
/// </summary>
public record SourceSnapshot(
    IReadOnlyList<SourceDavItem> DavItems,
    IReadOnlyList<SourceDavNzbFile> DavNzbFiles,
    IReadOnlyList<SourceDavMultipartFile> DavMultipartFiles,
    IReadOnlyList<SourceDavRarFile> DavRarFiles,
    IReadOnlyList<SourceQueueItem> QueueItems,
    IReadOnlyList<SourceQueueNzbContents> QueueNzbContents,
    IReadOnlyList<SourceHistoryItem> HistoryItems,
    IReadOnlyList<SourceConfigItem> ConfigItems,
    IReadOnlyList<SourceAccount> Accounts,
    IReadOnlyList<SourceHealthCheckResult> HealthCheckResults,
    IReadOnlyList<SourceHealthCheckStat> HealthCheckStats,
    IReadOnlyList<IncompatibleTableRow> LocalLinks,
    IReadOnlyList<IncompatibleTableRow> AnalysisHistoryItems,
    IReadOnlyList<IncompatibleTableRow> BandwidthSamples,
    IReadOnlyList<IncompatibleTableRow> MissingArticleEvents,
    IReadOnlyList<IncompatibleTableRow> MissingArticleSummaries,
    IReadOnlyList<IncompatibleTableRow> NzbProviderStats,
    IReadOnlyList<IncompatibleTableRow> ProviderBenchmarkResults);
