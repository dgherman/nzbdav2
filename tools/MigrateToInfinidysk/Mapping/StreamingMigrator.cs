using System.Text.Json;
using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// Streaming counterpart to <see cref="Migrator"/>, used by Program.cs for the real --apply/
/// --dry-run run against a live database. Migrator.Run (kept as-is, used only by
/// MigrateToInfinidysk.Tests' fixture-based unit tests) reads the whole source database into a
/// SourceSnapshot, builds full target row lists, AND reuses the source lists inside a new
/// ArchivePayload - so for the run's whole duration, the source read, the mapped target rows,
/// and the archive payload are all fully materialized in memory simultaneously. On real-world
/// databases (thousands of AnalysisHistoryItems/BandwidthSamples rows, hundreds of
/// QueueNzbContents rows each carrying full NZB XML text, thousands of skipped
/// DavMultipartFiles rows each carrying FileParts) that pattern is what causes --apply to OOM
/// even under an 8GB container limit.
///
/// This type instead streams each of the large tables (DavMultipartFiles/DavRarFiles,
/// AnalysisHistoryItems, BandwidthSamples, QueueNzbContents, HistoryItems, and the other
/// wholly-incompatible generic tables) row by row from the source SqliteDataReader straight to
/// its destination - either a TargetWriteSession insert or a JsonArchiveWriter.StreamingSession
/// array entry - without ever holding the full table, in any form, in memory at once. Only a
/// current row (transient, garbage-collected as soon as the next row is read) and a handful of
/// small lookup structures survive across the whole run:
///   - davItemsById: a dictionary of the small DavItems table (~thousands of rows, but each row
///     is a handful of scalar fields - no large blobs), needed for random-access FileSize lookups
///     while streaming DavNzbFiles, and for the Type/SubType remap while streaming DavItems
///     itself.
///   - skippedMultipartIds / nzbWrappedAsMultipartIds: Guid-only HashSets (tens of bytes per
///     entry), needed to decide, while later streaming DavItems, whether a given DavItem's
///     backing file was skipped (obfuscation) or wrapped (NZB-with-fallbacks) - see Migrator.Run
///     for why those decisions affect which DavItems rows get written and with what SubType.
/// QueueItems is the one table intentionally still read eagerly in full: infinidysk's SortOrder
/// backfill formula (QueueSortOrderCalculator) is a ROW_NUMBER() OVER (PARTITION BY Priority
/// ORDER BY CreatedAt, Id) window function - it inherently needs every row in a priority group
/// before it can assign any one row's SortOrder, so there is no streaming equivalent. QueueItems
/// is not one of the tables the real-world OOM report named as large, so this is a deliberate,
/// justified exception rather than a blanket "everything stays a list".
///
/// Same archive-then-DB-commit atomicity as MigrationApplier (round 3): the archive is written,
/// in full, to a temp file during the single streaming pass, and only after that pass completes
/// without error is the temp file renamed onto the final --archive-path and the target
/// transaction committed. Any exception during the pass leaves the temp archive file deleted and
/// the target transaction rolled back (never committed) - a failure partway through still leaves
/// zero partial writes, exactly like the old MigrationApplier/SqliteTargetWriter.Apply pairing.
/// </summary>
public static class StreamingMigrator
{
    public static MigrationResult Run(
        SqliteConnection sourceConn, SqliteConnection? targetConn, string? archivePath, MigrationOptions options)
    {
        var apply = targetConn != null;
        var errors = new List<string>();
        var warnings = new List<string>();
        var counts = new Dictionary<string, TableCounts>();

        // --- Accounts: small table, read eagerly (as Migrator.Run does). ---
        var sourceAccounts = ReadAccounts(sourceConn);
        var adminUsernames = sourceAccounts.Where(a => a.Type == 1).Select(a => a.Username).ToList();
        var adminSelection = AdminSelector.Select(adminUsernames, options.RequestedAdminUsername);
        if (!adminSelection.IsSuccess && adminUsernames.Count > 0)
            errors.Add(adminSelection.ErrorMessage!);

        if (errors.Count > 0)
            return MigrationResult.Failed(errors.ToArray());

        var targetAccounts = sourceAccounts
            .Where(a => a.Type != 1 || a.Username == adminSelection.SelectedUsername)
            .Select(a => new TargetAccount(a.Type, a.Username, a.PasswordHash, a.RandomSalt))
            .ToList();
        counts["Accounts"] = new TableCounts(targetAccounts.Count, sourceAccounts.Count - targetAccounts.Count, 0);

        // --- DavItems: small table (a few thousand rows of scalar fields, no blobs) - read
        //     eagerly into a dictionary. Needed for random-access FileSize lookups while
        //     streaming DavNzbFiles below, and to decide each DavItem's own Type/SubType/
        //     inclusion once skippedMultipartIds/nzbWrappedAsMultipartIds are known. ---
        var sourceDavItems = ReadDavItems(sourceConn);
        var davItemsById = sourceDavItems.ToDictionary(i => i.Id);

        string? tempArchivePath = null;
        JsonArchiveWriter.StreamingSession? archive = null;
        SqliteTargetWriter.TargetWriteSession? target = null;

        try
        {
            if (apply)
            {
                tempArchivePath = archivePath + $".tmp-{Guid.NewGuid():N}";
                archive = new JsonArchiveWriter.StreamingSession(tempArchivePath);
                archive.WriteHeader();
                target = new SqliteTargetWriter.TargetWriteSession(targetConn!);
            }

            if (apply)
            {
                foreach (var a in targetAccounts) target!.InsertAccount(a);
            }

            // --- DavMultipartFiles (+ legacy DavRarFiles): obfuscation-key rows skipped and
            //     archived. Same decision as Migrator.Run/MapMultipart, applied per row as it's
            //     streamed rather than after building a full list of source rows. ---
            var davMultipartCopied = 0;
            var skippedMultipartIds = new HashSet<Guid>();
            var multipartArchiveCount = 0;
            if (apply) archive!.BeginArray("SkippedObfuscatedFiles");
            foreach (var mp in SqliteSourceReader.StreamDavMultipartFiles(sourceConn))
                StreamOneMultipart(mp, apply, archive, target, warnings, skippedMultipartIds, ref davMultipartCopied, ref multipartArchiveCount);
            foreach (var rar in SqliteSourceReader.StreamDavRarFiles(sourceConn))
                StreamOneMultipart(MultipartFileMapper.FromRarFile(rar), apply, archive, target, warnings, skippedMultipartIds, ref davMultipartCopied, ref multipartArchiveCount);
            if (apply) archive!.EndArray();

            // --- DavNzbFiles: wrap as a single-part DavMultipartFiles row when the source
            //     DavItems row has a valid FileSize (so fallback IDs land somewhere infinidysk
            //     reads at playback); otherwise write a plain DavNzbFiles row and archive the
            //     fallback IDs. See Migrator.Run for the full rationale - identical decision
            //     logic, applied per row as it streams. ---
            var davNzbCopied = 0;
            var nzbWrappedAsMultipartIds = new HashSet<Guid>();
            var nzbFallbackArchiveCount = 0;
            if (apply) archive!.BeginArray("DavNzbFileFallbackIds");
            foreach (var f in SqliteSourceReader.StreamDavNzbFiles(sourceConn))
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
                        SegmentByteRanges: null, SegmentFallbackIds: aligned, IsSplitAfter: null, SegmentByteRangesTrusted: null);
                    var meta = new TargetMultipartMeta(
                        AesParams: null, FileParts: [filePart], IsLazy: false, PathInArchive: null,
                        ArchivePassword: null, PendingParts: [], ExpectedFileSize: null);

                    if (apply)
                        target!.InsertDavMultipartFile(new TargetDavMultipartFile(f.Id, JsonSerializer.Serialize(meta, (JsonSerializerOptions?)null)));
                    nzbWrappedAsMultipartIds.Add(f.Id);
                    davMultipartCopied++;
                }
                else
                {
                    if (apply)
                        target!.InsertDavNzbFile(new TargetDavNzbFile(f.Id, JsonSerializer.Serialize(f.SegmentIds, (JsonSerializerOptions?)null)));
                    davNzbCopied++;

                    var aligned = SegmentFallbackMapper.ToAlignedArray(f.SegmentFallbacks, f.SegmentIds.Length);
                    if (aligned != null)
                    {
                        if (apply) archive!.WriteItem(new ArchivedNzbFileFallback(f.Id, f.SegmentIds, aligned));
                        nzbFallbackArchiveCount++;
                        warnings.Add(
                            $"DavNzbFiles.Id={f.Id}: has SegmentFallbacks but no valid source FileSize (DavItems " +
                            "row missing, or FileSize null/non-positive) - can't wrap into a playable multipart " +
                            "row with a real byte range, so the fallback IDs are archived only, not written to a " +
                            "row infinidysk will actually read at playback.");
                    }
                }
            }
            if (apply) archive!.EndArray();
            counts["DavNzbFiles"] = new TableCounts(davNzbCopied, 0, nzbFallbackArchiveCount);
            counts["DavMultipartFiles"] = new TableCounts(davMultipartCopied, skippedMultipartIds.Count, multipartArchiveCount);

            // --- DavItems: Type/SubType mapping, fixed-root merge, skip rows whose backing
            //     multipart payload was skipped above. ---
            var davItemsCopied = 0;
            foreach (var item in sourceDavItems)
            {
                if (skippedMultipartIds.Contains(item.Id)) continue;
                var (type, subType) = DavItemTypeMapper.Map(item.Id, (DavItemTypeMapper.LegacyType)item.Type);
                if (nzbWrappedAsMultipartIds.Contains(item.Id))
                    (type, subType) = (2, 203);
                if (apply)
                {
                    target!.InsertDavItem(new TargetDavItem(
                        item.Id, item.IdPrefix, item.CreatedAtUnixSeconds, item.ParentId, item.Name, item.FileSize,
                        type, subType, item.Path, item.ReleaseDateUnixSeconds, item.LastHealthCheckUnixSeconds,
                        item.NextHealthCheckUnixSeconds, HealthRepairPending: false, item.HistoryItemId));
                }
                davItemsCopied++;
            }
            counts["DavItems"] = new TableCounts(davItemsCopied, skippedMultipartIds.Count, 0);

            // --- QueueItems: needs the full list for SortOrder's window-function backfill - see
            //     class doc comment for why this table is a deliberate, justified exception. ---
            var sourceQueueItems = ReadQueueItems(sourceConn);
            var sortOrders = QueueSortOrderCalculator.Backfill(
                sourceQueueItems.Select(q => new QueueSortOrderCalculator.Item(
                    q.Id, q.Priority, DateTimeOffset.FromUnixTimeSeconds(q.CreatedAtUnixSeconds).UtcDateTime)));
            foreach (var q in sourceQueueItems)
            {
                if (apply)
                {
                    target!.InsertQueueItem(new TargetQueueItem(
                        q.Id, q.CreatedAtUnixSeconds, sortOrders[q.Id], q.FileName, q.JobName, q.NzbFileSize,
                        q.TotalSegmentBytes, q.Category, q.Priority, q.PostProcessing, q.PauseUntilUnixSeconds));
                }
            }
            counts["QueueItems"] = new TableCounts(sourceQueueItems.Count, 0, 0);

            // --- QueueNzbContents: streamed - each row can carry the full NZB XML text of a
            //     release (potentially large), and real-world databases can have hundreds of
            //     these. No archive contribution, no ordering dependency - pure 1:1 stream. ---
            var queueNzbCopied = 0;
            foreach (var q in SqliteSourceReader.StreamQueueNzbContents(sourceConn))
            {
                if (apply) target!.InsertQueueNzbContents(new TargetQueueNzbContents(q.Id, q.NzbContents));
                queueNzbCopied++;
            }
            counts["QueueNzbContents"] = new TableCounts(queueNzbCopied, 0, 0);

            // --- HistoryItems: streamed - real-world databases can have thousands of these.
            //     Each row can produce both a target row (always) and an archive entry (only
            //     when it carries nzbdav2-only fields). ---
            var historyCopied = 0;
            var historyArchiveCount = 0;
            if (apply) archive!.BeginArray("HistoryItemDroppedFields");
            foreach (var h in SqliteSourceReader.StreamHistoryItems(sourceConn))
            {
                if (apply)
                {
                    target!.InsertHistoryItem(new TargetHistoryItem(
                        h.Id, h.CreatedAtUnixSeconds, h.Category, h.DownloadStatus, h.DownloadTimeSeconds,
                        h.FailMessage, h.FileName, h.JobName, h.TotalSegmentBytes, h.DownloadDirId));
                }
                historyCopied++;

                var hasDroppedData = h.IsHidden || h.HiddenAtUnixSeconds != null || h.NzbContents != null ||
                                      h.FailureReason != null || h.IsImported || h.IsArchived || h.ArchivedAtUnixSeconds != null;
                if (hasDroppedData)
                {
                    if (apply)
                    {
                        archive!.WriteItem(new ArchivedHistoryItemFields(
                            h.Id, h.IsHidden, h.HiddenAtUnixSeconds, h.NzbContents, h.FailureReason,
                            h.IsImported, h.IsArchived, h.ArchivedAtUnixSeconds));
                    }
                    historyArchiveCount++;
                }
            }
            if (apply) archive!.EndArray();
            counts["HistoryItems"] = new TableCounts(historyCopied, 0, historyArchiveCount);

            // --- ConfigItems: small table, filtered eagerly (as Migrator.Run does). ---
            var sourceConfigItems = ReadConfigItems(sourceConn);
            var configResult = ConfigItemFilter.Filter(sourceConfigItems.Select(c => (c.ConfigName, c.ConfigValue)));
            if (apply)
            {
                foreach (var c in configResult.Copy)
                    target!.InsertConfigItem(new TargetConfigItem(c.Name, c.Value));
                archive!.BeginArray("SkippedConfigItems");
                foreach (var c in configResult.Skip)
                    archive.WriteItem(new { ConfigName = c.Name, ConfigValue = c.Value });
                archive.EndArray();
            }
            counts["ConfigItems"] = new TableCounts(configResult.Copy.Count, configResult.Skip.Count, configResult.Skip.Count);
            foreach (var skipped in configResult.Skip)
                warnings.Add($"ConfigItems: skipping unrecognized key '{skipped.Name}' (not read by infinidysk; see MIGRATING_TO_INFINIDYSK.md).");

            // --- HealthCheckResults: modest table, streamed for consistency (not called out in
            //     the real-world OOM report, but the per-row insert/archive pattern is identical
            //     and cheap to stream). ---
            var healthCheckCopied = 0;
            var healthCheckArchiveCount = 0;
            if (apply) archive!.BeginArray("HealthCheckDroppedOperations");
            foreach (var h in ReadHealthCheckResults(sourceConn))
            {
                if (apply)
                {
                    target!.InsertHealthCheckResult(new TargetHealthCheckResult(
                        h.Id, h.CreatedAtUnixSeconds, h.DavItemId, h.Path, h.Result, h.RepairStatus, h.Message));
                }
                healthCheckCopied++;

                if (h.Operation != "UNKNOWN")
                {
                    if (apply) archive!.WriteItem(new ArchivedHealthCheckOperation(h.Id, h.Operation));
                    healthCheckArchiveCount++;
                }
            }
            if (apply) archive!.EndArray();
            counts["HealthCheckResults"] = new TableCounts(healthCheckCopied, 0, healthCheckArchiveCount);

            // --- HealthCheckStats: small table, eager. ---
            var healthCheckStatsCopied = 0;
            foreach (var h in ReadHealthCheckStats(sourceConn))
            {
                if (apply)
                    target!.InsertHealthCheckStat(new TargetHealthCheckStat(h.DateStartInclusiveUnixSeconds, h.DateEndExclusiveUnixSeconds, h.Result, h.RepairStatus, h.Count));
                healthCheckStatsCopied++;
            }
            counts["HealthCheckStats"] = new TableCounts(healthCheckStatsCopied, 0, 0);

            // --- Wholly-incompatible generic tables: archive-only, streamed - AnalysisHistoryItems
            //     and BandwidthSamples in particular can run into the thousands of rows on a
            //     real-world database. ---
            StreamIncompatibleTable(sourceConn, "LocalLinks", apply, archive, counts);
            StreamIncompatibleTable(sourceConn, "AnalysisHistoryItems", apply, archive, counts);
            StreamIncompatibleTable(sourceConn, "BandwidthSamples", apply, archive, counts);
            StreamIncompatibleTable(sourceConn, "MissingArticleEvents", apply, archive, counts);
            StreamIncompatibleTable(sourceConn, "MissingArticleSummaries", apply, archive, counts);
            StreamIncompatibleTable(sourceConn, "NzbProviderStats", apply, archive, counts);
            StreamIncompatibleTable(sourceConn, "ProviderBenchmarkResults", apply, archive, counts);

            if (apply)
            {
                // Archive fully written - finalize and publish it BEFORE the DB commit (same
                // ordering guarantee as MigrationApplier: the commit is the true point of no
                // return, and everything before it, including the archive publish, is either
                // fully done or fully rolled back before that point).
                archive!.Finish();
                archive.Dispose();
                archive = null;
                File.Move(tempArchivePath!, archivePath!, overwrite: true);
                tempArchivePath = null;

                target!.Commit();
            }

            return new MigrationResult(
                Success: true, Errors: errors, Warnings: warnings, Counts: counts,
                DavItems: [], DavNzbFiles: [], DavMultipartFiles: [], QueueItems: [], QueueNzbContents: [],
                HistoryItems: [], ConfigItems: [], Accounts: [], HealthCheckResults: [], HealthCheckStats: [],
                Archive: new ArchivePayload([], [], [], [], [], [], [], [], [], [], [], []));
        }
        finally
        {
            archive?.Dispose();
            target?.Dispose(); // no-op if already committed; rolls back if an exception was thrown first
            if (tempArchivePath != null && File.Exists(tempArchivePath))
            {
                try { File.Delete(tempArchivePath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private static void StreamOneMultipart(
        Model.SourceDavMultipartFile mp, bool apply,
        JsonArchiveWriter.StreamingSession? archive, SqliteTargetWriter.TargetWriteSession? target, List<string> warnings,
        HashSet<Guid> skippedIds, ref int copiedCount, ref int archivedCount)
    {
        var mapped = MultipartFileMapper.Map(mp, wasRarSourced: true);
        if (mapped.IsUnmigratable)
        {
            skippedIds.Add(mp.Id);
            warnings.Add(mapped.SkipReason!);
            if (apply)
            {
                archive!.WriteItem(new ArchivedObfuscatedFile(
                    DavItemId: mp.Id,
                    Reason: mapped.SkipReason!,
                    ObfuscationKeyBase64: mp.ObfuscationKey == null ? null : Convert.ToBase64String(mp.ObfuscationKey),
                    SourceMetadataJson: mp.RawMetadataJson ?? JsonSerializer.Serialize(mp, (JsonSerializerOptions?)null)));
            }
            archivedCount++;
            return;
        }

        if (apply)
        {
            var json = JsonSerializer.Serialize(mapped.Meta, (JsonSerializerOptions?)null);
            target!.InsertDavMultipartFile(new TargetDavMultipartFile(mp.Id, json));
        }
        copiedCount++;
    }

    private static void StreamIncompatibleTable(
        SqliteConnection sourceConn, string tableName, bool apply,
        JsonArchiveWriter.StreamingSession? archive, Dictionary<string, TableCounts> counts)
    {
        var count = 0;
        if (apply) archive!.BeginArray(tableName);
        foreach (var row in SqliteSourceReader.StreamGenericTable(sourceConn, tableName))
        {
            if (apply) archive!.WriteItem(row);
            count++;
        }
        if (apply) archive!.EndArray();
        counts[tableName] = new TableCounts(0, count, count);
    }

    // Thin re-reads of the small tables, kept private here rather than exposed on
    // SqliteSourceReader again - identical SQL to Migrator.Run's callers, just scoped locally so
    // this file's dependency surface stays limited to what it actually streams.
    private static List<Model.SourceAccount> ReadAccounts(SqliteConnection conn) =>
        QueryList(conn, "SELECT Type, Username, PasswordHash, RandomSalt FROM Accounts ORDER BY rowid",
            r => new Model.SourceAccount(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)));

    private static List<Model.SourceDavItem> ReadDavItems(SqliteConnection conn) =>
        QueryList(conn, """
            SELECT Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, Path, ReleaseDate,
                   LastHealthCheck, NextHealthCheck, MediaInfo, IsCorrupted, CorruptionReason, HistoryItemId
            FROM DavItems
            """,
            r => new Model.SourceDavItem(
                Id: r.GetGuid(0), IdPrefix: r.GetString(1), CreatedAtUnixSeconds: ToUnixSeconds(r, 2),
                ParentId: r.IsDBNull(3) ? null : r.GetGuid(3), Name: r.GetString(4),
                FileSize: r.IsDBNull(5) ? null : r.GetInt64(5), Type: r.GetInt32(6), Path: r.GetString(7),
                ReleaseDateUnixSeconds: r.IsDBNull(8) ? null : r.GetInt64(8),
                LastHealthCheckUnixSeconds: r.IsDBNull(9) ? null : r.GetInt64(9),
                NextHealthCheckUnixSeconds: r.IsDBNull(10) ? null : r.GetInt64(10),
                MediaInfo: r.IsDBNull(11) ? null : r.GetString(11), IsCorrupted: !r.IsDBNull(12) && r.GetBoolean(12),
                CorruptionReason: r.IsDBNull(13) ? null : r.GetString(13),
                HistoryItemId: r.IsDBNull(14) ? null : r.GetGuid(14)));

    private static List<Model.SourceQueueItem> ReadQueueItems(SqliteConnection conn) =>
        QueryList(conn, """
            SELECT Id, CreatedAt, FileName, JobName, NzbFileSize, TotalSegmentBytes, Category,
                   Priority, PostProcessing, PauseUntil
            FROM QueueItems
            """,
            r => new Model.SourceQueueItem(
                r.GetGuid(0), ToUnixSeconds(r, 1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5),
                r.GetString(6), r.GetInt32(7), r.GetInt32(8),
                r.IsDBNull(9) ? null : ToUnixSecondsValue(r.GetDateTime(9))));

    private static List<Model.SourceConfigItem> ReadConfigItems(SqliteConnection conn) =>
        QueryList(conn, "SELECT ConfigName, ConfigValue FROM ConfigItems",
            r => new Model.SourceConfigItem(r.GetString(0), r.GetString(1)));

    private static List<Model.SourceHealthCheckResult> ReadHealthCheckResults(SqliteConnection conn)
    {
        if (!TableExists(conn, "HealthCheckResults")) return [];
        return QueryList(conn, "SELECT Id, CreatedAt, DavItemId, Path, Result, RepairStatus, Message, Operation FROM HealthCheckResults",
            r => new Model.SourceHealthCheckResult(
                r.GetGuid(0), r.GetInt64(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? "UNKNOWN" : r.GetString(7)));
    }

    private static List<Model.SourceHealthCheckStat> ReadHealthCheckStats(SqliteConnection conn)
    {
        if (!TableExists(conn, "HealthCheckStats")) return [];
        return QueryList(conn, "SELECT DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count FROM HealthCheckStats",
            r => new Model.SourceHealthCheckStat(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static List<T> QueryList<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> project)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(project(reader));
        return result;
    }

    private static long ToUnixSeconds(SqliteDataReader reader, int ordinal) => ToUnixSecondsValue(reader.GetDateTime(ordinal));
    private static long ToUnixSecondsValue(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
