using System.Text.Json;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// Pure in-memory mapping of a nzbdav2 SourceSnapshot into infinidysk-shaped rows. Contains no
/// I/O - SqliteSourceReader builds the SourceSnapshot, SqliteTargetWriter applies the result.
/// </summary>
public static class Migrator
{
    public static MigrationResult Run(SourceSnapshot source, MigrationOptions options)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var counts = new Dictionary<string, TableCounts>();

        // --- Accounts: infinidysk allows at most one Admin account. ---
        var adminUsernames = source.Accounts
            .Where(a => a.Type == 1) // Account.AccountType.Admin
            .Select(a => a.Username)
            .ToList();
        var adminSelection = AdminSelector.Select(adminUsernames, options.RequestedAdminUsername);
        if (!adminSelection.IsSuccess && adminUsernames.Count > 0)
            errors.Add(adminSelection.ErrorMessage!);

        var targetAccounts = new List<TargetAccount>();
        foreach (var account in source.Accounts)
        {
            if (account.Type == 1 && account.Username != adminSelection.SelectedUsername)
                continue; // dropped non-selected admin, see adminSelection above
            targetAccounts.Add(new TargetAccount(account.Type, account.Username, account.PasswordHash, account.RandomSalt));
        }
        counts["Accounts"] = new TableCounts(targetAccounts.Count, source.Accounts.Count - targetAccounts.Count, 0);

        if (errors.Count > 0)
            return MigrationResult.Failed(errors.ToArray());

        // --- DavItems: Type/SubType mapping, fixed-root merge by ID. ---
        var targetDavItems = source.DavItems.Select(MapDavItem).ToList();
        counts["DavItems"] = new TableCounts(targetDavItems.Count, 0, 0);

        // --- DavNzbFiles: SegmentIds carry over as plain JSON; SegmentFallbacks/SegmentSizes
        //     have no SQL-column destination for this table (infinidysk keeps them only in its
        //     external blob store, reached via NzbBlobId - left null so infinidysk lazily
        //     migrates/re-probes on first access). ---
        var targetDavNzbFiles = source.DavNzbFiles
            .Select(f => new TargetDavNzbFile(f.Id, JsonSerializer.Serialize(f.SegmentIds, (JsonSerializerOptions?)null)))
            .ToList();
        counts["DavNzbFiles"] = new TableCounts(targetDavNzbFiles.Count, 0, 0);

        // --- DavMultipartFiles (+ legacy DavRarFiles merged in): obfuscation-key rows flagged
        //     and skipped rather than silently imported with broken playback. ---
        var targetDavMultipartFiles = new List<TargetDavMultipartFile>();
        var skippedObfuscated = new List<string>();
        foreach (var mp in source.DavMultipartFiles)
            MapMultipart(mp, wasRarSourced: false, targetDavMultipartFiles, skippedObfuscated);
        foreach (var rar in source.DavRarFiles)
            MapMultipart(MultipartFileMapper.FromRarFile(rar), wasRarSourced: true, targetDavMultipartFiles, skippedObfuscated);
        counts["DavMultipartFiles"] = new TableCounts(
            targetDavMultipartFiles.Count, skippedObfuscated.Count, 0);
        warnings.AddRange(skippedObfuscated);

        // --- QueueItems: SortOrder backfilled per infinidysk's own formula. ---
        var sortOrders = QueueSortOrderCalculator.Backfill(
            source.QueueItems.Select(q => new QueueSortOrderCalculator.Item(
                q.Id, q.Priority, DateTimeOffset.FromUnixTimeSeconds(q.CreatedAtUnixSeconds).UtcDateTime)));
        var targetQueueItems = source.QueueItems.Select(q => new TargetQueueItem(
            q.Id, q.CreatedAtUnixSeconds, sortOrders[q.Id], q.FileName, q.JobName, q.NzbFileSize,
            q.TotalSegmentBytes, q.Category, q.Priority, q.PostProcessing, q.PauseUntilUnixSeconds)).ToList();
        counts["QueueItems"] = new TableCounts(targetQueueItems.Count, 0, 0);

        var targetQueueNzbContents = source.QueueNzbContents
            .Select(q => new TargetQueueNzbContents(q.Id, q.NzbContents)).ToList();
        counts["QueueNzbContents"] = new TableCounts(targetQueueNzbContents.Count, 0, 0);

        // --- HistoryItems: shared columns copied; nzbdav2-only fields archived. ---
        var targetHistoryItems = new List<TargetHistoryItem>();
        var archivedHistoryFields = new List<ArchivedHistoryItemFields>();
        foreach (var h in source.HistoryItems)
        {
            targetHistoryItems.Add(new TargetHistoryItem(
                h.Id, h.CreatedAtUnixSeconds, h.Category, h.DownloadStatus, h.DownloadTimeSeconds,
                h.FailMessage, h.FileName, h.JobName, h.TotalSegmentBytes, h.DownloadDirId));

            var hasDroppedData = h.IsHidden || h.HiddenAtUnixSeconds != null || h.NzbContents != null ||
                                  h.FailureReason != null || h.IsImported || h.IsArchived || h.ArchivedAtUnixSeconds != null;
            if (hasDroppedData)
            {
                archivedHistoryFields.Add(new ArchivedHistoryItemFields(
                    h.Id, h.IsHidden, h.HiddenAtUnixSeconds, h.NzbContents, h.FailureReason,
                    h.IsImported, h.IsArchived, h.ArchivedAtUnixSeconds));
            }
        }
        counts["HistoryItems"] = new TableCounts(targetHistoryItems.Count, 0, archivedHistoryFields.Count);

        // --- ConfigItems: only infinidysk-recognized keys copied. ---
        var configResult = ConfigItemFilter.Filter(source.ConfigItems.Select(c => (c.ConfigName, c.ConfigValue)));
        var targetConfigItems = configResult.Copy.Select(c => new TargetConfigItem(c.Item1, c.Item2)).ToList();
        counts["ConfigItems"] = new TableCounts(targetConfigItems.Count, configResult.Skip.Count, configResult.Skip.Count);
        foreach (var skipped in configResult.Skip)
            warnings.Add($"ConfigItems: skipping unrecognized key '{skipped.Item1}' (not read by infinidysk; see MIGRATING_TO_INFINIDYSK.md).");

        // --- HealthCheckResults: Operation dropped (archived), everything else copied. ---
        var targetHealthCheckResults = source.HealthCheckResults.Select(h => new TargetHealthCheckResult(
            h.Id, h.CreatedAtUnixSeconds, h.DavItemId, h.Path, h.Result, h.RepairStatus, h.Message)).ToList();
        var archivedHealthCheckOps = source.HealthCheckResults
            .Where(h => h.Operation != "UNKNOWN")
            .Select(h => new ArchivedHealthCheckOperation(h.Id, h.Operation)).ToList();
        counts["HealthCheckResults"] = new TableCounts(targetHealthCheckResults.Count, 0, archivedHealthCheckOps.Count);

        // --- HealthCheckStats: identical shape, copied as-is. ---
        var targetHealthCheckStats = source.HealthCheckStats.Select(h => new TargetHealthCheckStat(
            h.DateStartInclusiveUnixSeconds, h.DateEndExclusiveUnixSeconds, h.Result, h.RepairStatus, h.Count)).ToList();
        counts["HealthCheckStats"] = new TableCounts(targetHealthCheckStats.Count, 0, 0);

        // --- Wholly-incompatible tables: archived only. ---
        counts["LocalLinks"] = new TableCounts(0, source.LocalLinks.Count, source.LocalLinks.Count);
        counts["AnalysisHistoryItems"] = new TableCounts(0, source.AnalysisHistoryItems.Count, source.AnalysisHistoryItems.Count);
        counts["BandwidthSamples"] = new TableCounts(0, source.BandwidthSamples.Count, source.BandwidthSamples.Count);
        counts["MissingArticleEvents"] = new TableCounts(0, source.MissingArticleEvents.Count, source.MissingArticleEvents.Count);
        counts["MissingArticleSummaries"] = new TableCounts(0, source.MissingArticleSummaries.Count, source.MissingArticleSummaries.Count);
        counts["NzbProviderStats"] = new TableCounts(0, source.NzbProviderStats.Count, source.NzbProviderStats.Count);
        counts["ProviderBenchmarkResults"] = new TableCounts(0, source.ProviderBenchmarkResults.Count, source.ProviderBenchmarkResults.Count);

        var archive = new ArchivePayload(
            source.LocalLinks, source.AnalysisHistoryItems, source.BandwidthSamples, source.MissingArticleEvents,
            source.MissingArticleSummaries, source.NzbProviderStats, source.ProviderBenchmarkResults,
            archivedHistoryFields, archivedHealthCheckOps, configResult.Skip, skippedObfuscated);

        return new MigrationResult(
            Success: true, Errors: errors, Warnings: warnings, Counts: counts,
            DavItems: targetDavItems, DavNzbFiles: targetDavNzbFiles, DavMultipartFiles: targetDavMultipartFiles,
            QueueItems: targetQueueItems, QueueNzbContents: targetQueueNzbContents, HistoryItems: targetHistoryItems,
            ConfigItems: targetConfigItems, Accounts: targetAccounts, HealthCheckResults: targetHealthCheckResults,
            HealthCheckStats: targetHealthCheckStats, Archive: archive);
    }

    private static TargetDavItem MapDavItem(SourceDavItem item)
    {
        var (type, subType) = DavItemTypeMapper.Map(item.Id, (DavItemTypeMapper.LegacyType)item.Type);
        return new TargetDavItem(
            item.Id, item.IdPrefix, item.CreatedAtUnixSeconds, item.ParentId, item.Name, item.FileSize,
            type, subType, item.Path, item.ReleaseDateUnixSeconds, item.LastHealthCheckUnixSeconds,
            item.NextHealthCheckUnixSeconds, HealthRepairPending: false, item.HistoryItemId);
    }

    private static void MapMultipart(
        SourceDavMultipartFile mp, bool wasRarSourced,
        List<TargetDavMultipartFile> targetRows, List<string> skippedReasons)
    {
        var mapped = MultipartFileMapper.Map(mp, wasRarSourced);
        if (mapped.IsUnmigratable)
        {
            skippedReasons.Add(mapped.SkipReason!);
            return;
        }

        var json = JsonSerializer.Serialize(mapped.Meta, (JsonSerializerOptions?)null);
        targetRows.Add(new TargetDavMultipartFile(mp.Id, json));
    }
}
