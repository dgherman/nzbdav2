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

        var davItemsById = source.DavItems.ToDictionary(i => i.Id);

        // --- DavMultipartFiles (+ legacy DavRarFiles merged in): obfuscation-key rows flagged
        //     and skipped rather than silently imported with broken playback.
        //
        //     Native DavMultipartFiles rows (wasRarSourced: false) and legacy DavRarFiles rows
        //     (wasRarSourced: true) are treated differently here, not identically as earlier
        //     rounds did: a native row's null ObfuscationKey is a normal, common case (most
        //     multipart files never went through RAR at all - MultipartMkvProcessor and
        //     SevenZipProcessor both produce multipart rows too, and neither ever sets an
        //     obfuscation key), so it maps through normally. A RAR-converted row is different:
        //     nzbdav2 detects RAR obfuscation by content-sniffing at read time
        //     (RarDeobfuscationStream), not from a stored flag, so a null key on a RAR-converted
        //     row is NOT proof the bytes are unobfuscated - it only means no key was captured.
        //     Those rows keep going through the conservative skip-and-archive path below
        //     regardless of key-nullness. This is a permanent limitation, not a gap to close:
        //     infinidysk has no XOR-deobfuscation support to hand such content to even if this
        //     tool could prove it needed one. See MIGRATING_TO_INFINIDYSK.md.
        var targetDavMultipartFiles = new List<TargetDavMultipartFile>();
        var skippedMultipartIds = new HashSet<Guid>();
        var archivedObfuscated = new List<ArchivedObfuscatedFile>();
        foreach (var mp in source.DavMultipartFiles)
            MapMultipart(mp, wasRarSourced: false, targetDavMultipartFiles, skippedMultipartIds, archivedObfuscated, warnings);
        foreach (var rar in source.DavRarFiles)
            MapMultipart(MultipartFileMapper.FromRarFile(rar), wasRarSourced: true, targetDavMultipartFiles, skippedMultipartIds, archivedObfuscated, warnings);

        // --- DavNzbFiles: SegmentIds carry over as plain JSON... but SegmentFallbacks
        //     (alternate message IDs for duplicate-numbered segments - a real retry path used
        //     when the primary article is missing, not a re-derivable optimization) have no SQL
        //     column on infinidysk's DavNzbFiles table (only Id/SegmentIds are EF-mapped there).
        //     So instead of writing a DavNzbFiles row and archiving the fallback IDs where
        //     nothing at playback time will ever read them, wrap the file as a single-part
        //     DavMultipartFiles row instead - fallback IDs live on FileParts[].SegmentFallbackIds
        //     there and are read at playback. This needs the source DavItems row's FileSize to
        //     build the single FilePart's byte range; when that's missing or non-positive (no
        //     matching DavItems row, or FileSize null/<=0), there's nothing safe to wrap around,
        //     so this falls back to the previous archive-only behavior with a warning instead of
        //     inventing a fake size. ---
        var targetDavNzbFiles = new List<TargetDavNzbFile>();
        var archivedNzbFallbacks = new List<ArchivedNzbFileFallback>();
        var nzbWrappedAsMultipartIds = new HashSet<Guid>();
        foreach (var f in source.DavNzbFiles)
        {
            davItemsById.TryGetValue(f.Id, out var davItem);
            var fileSize = davItem?.FileSize;

            if (fileSize is > 0)
            {
                var aligned = SegmentFallbackMapper.ToAlignedArray(f.SegmentFallbacks, f.SegmentIds.Length);
                var filePart = new TargetFilePart(
                    SegmentIds: f.SegmentIds,
                    SegmentIdByteRange: new TargetLongRange(0, fileSize.Value),
                    FilePartByteRange: new TargetLongRange(0, fileSize.Value),
                    SegmentByteRanges: null,
                    SegmentFallbackIds: aligned,
                    IsSplitAfter: null,
                    SegmentByteRangesTrusted: null);
                var meta = new TargetMultipartMeta(
                    AesParams: null, FileParts: [filePart], IsLazy: false, PathInArchive: null,
                    ArchivePassword: null, PendingParts: [], ExpectedFileSize: null);

                targetDavMultipartFiles.Add(new TargetDavMultipartFile(f.Id, JsonSerializer.Serialize(meta, (JsonSerializerOptions?)null)));
                nzbWrappedAsMultipartIds.Add(f.Id);
            }
            else
            {
                targetDavNzbFiles.Add(new TargetDavNzbFile(f.Id, JsonSerializer.Serialize(f.SegmentIds, (JsonSerializerOptions?)null)));

                var aligned = SegmentFallbackMapper.ToAlignedArray(f.SegmentFallbacks, f.SegmentIds.Length);
                if (aligned != null)
                {
                    archivedNzbFallbacks.Add(new ArchivedNzbFileFallback(f.Id, f.SegmentIds, aligned));
                    warnings.Add(
                        $"DavNzbFiles.Id={f.Id}: has SegmentFallbacks but no valid source FileSize (DavItems " +
                        "row missing, or FileSize null/non-positive) - can't wrap into a playable multipart " +
                        "row with a real byte range, so the fallback IDs are archived only, not written to a " +
                        "row infinidysk will actually read at playback.");
                }
            }
        }
        counts["DavNzbFiles"] = new TableCounts(targetDavNzbFiles.Count, 0, archivedNzbFallbacks.Count);
        counts["DavMultipartFiles"] = new TableCounts(targetDavMultipartFiles.Count, skippedMultipartIds.Count, archivedObfuscated.Count);

        // --- DavItems: Type/SubType mapping, fixed-root merge by ID. A DavItem whose backing
        //     DavMultipartFiles payload was skipped above (obfuscation-key) is skipped too - an
        //     entry with no backing file metadata would otherwise appear in the target library
        //     with nothing playable behind it. A DavItem wrapped into DavMultipartFiles above
        //     (NZB-with-fallbacks case) needs SubType 203, not the NzbFile default of 201,
        //     matching where its data actually ended up. See MIGRATING_TO_INFINIDYSK.md. ---
        var targetDavItems = source.DavItems
            .Where(i => !skippedMultipartIds.Contains(i.Id))
            .Select(i => MapDavItem(i, nzbWrappedAsMultipartIds.Contains(i.Id)))
            .ToList();
        counts["DavItems"] = new TableCounts(targetDavItems.Count, skippedMultipartIds.Count, 0);

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
            archivedHistoryFields, archivedHealthCheckOps, configResult.Skip, archivedObfuscated, archivedNzbFallbacks);

        return new MigrationResult(
            Success: true, Errors: errors, Warnings: warnings, Counts: counts,
            DavItems: targetDavItems, DavNzbFiles: targetDavNzbFiles, DavMultipartFiles: targetDavMultipartFiles,
            QueueItems: targetQueueItems, QueueNzbContents: targetQueueNzbContents, HistoryItems: targetHistoryItems,
            ConfigItems: targetConfigItems, Accounts: targetAccounts, HealthCheckResults: targetHealthCheckResults,
            HealthCheckStats: targetHealthCheckStats, Archive: archive);
    }

    private static TargetDavItem MapDavItem(SourceDavItem item, bool wrappedAsMultipart)
    {
        var (type, subType) = DavItemTypeMapper.Map(item.Id, (DavItemTypeMapper.LegacyType)item.Type);
        if (wrappedAsMultipart)
            (type, subType) = (2, 203); // MultipartFile - matches where the data actually lives, see caller
        return new TargetDavItem(
            item.Id, item.IdPrefix, item.CreatedAtUnixSeconds, item.ParentId, item.Name, item.FileSize,
            type, subType, item.Path, item.ReleaseDateUnixSeconds, item.LastHealthCheckUnixSeconds,
            item.NextHealthCheckUnixSeconds, HealthRepairPending: false, item.HistoryItemId);
    }

    private static void MapMultipart(
        SourceDavMultipartFile mp, bool wasRarSourced,
        List<TargetDavMultipartFile> targetRows, HashSet<Guid> skippedIds,
        List<ArchivedObfuscatedFile> archivedObfuscated, List<string> warnings)
    {
        var mapped = MultipartFileMapper.Map(mp, wasRarSourced);
        if (mapped.IsUnmigratable)
        {
            skippedIds.Add(mp.Id);
            warnings.Add(mapped.SkipReason!);
            archivedObfuscated.Add(new ArchivedObfuscatedFile(
                DavItemId: mp.Id,
                Reason: mapped.SkipReason!,
                ObfuscationKeyBase64: mp.ObfuscationKey == null ? null : Convert.ToBase64String(mp.ObfuscationKey),
                SourceMetadataJson: JsonSerializer.Serialize(mp, (JsonSerializerOptions?)null)));
            return;
        }

        var json = JsonSerializer.Serialize(mapped.Meta, (JsonSerializerOptions?)null);
        targetRows.Add(new TargetDavMultipartFile(mp.Id, json));
    }
}
