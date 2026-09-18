using Microsoft.Data.Sqlite;
using System.Text.Json;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>Reads a nzbdav2 db.sqlite (read-only) into a SourceSnapshot.</summary>
public static class SqliteSourceReader
{
    public static IReadOnlyList<string> ReadMigrationHistory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public static SourceSnapshot Read(SqliteConnection conn)
    {
        return new SourceSnapshot(
            DavItems: ReadDavItems(conn),
            DavNzbFiles: ReadDavNzbFiles(conn),
            DavMultipartFiles: ReadDavMultipartFiles(conn),
            DavRarFiles: ReadDavRarFiles(conn),
            QueueItems: ReadQueueItems(conn),
            QueueNzbContents: ReadQueueNzbContents(conn),
            HistoryItems: ReadHistoryItems(conn),
            ConfigItems: ReadConfigItems(conn),
            Accounts: ReadAccounts(conn),
            HealthCheckResults: ReadHealthCheckResults(conn),
            HealthCheckStats: ReadHealthCheckStats(conn),
            LocalLinks: TableExists(conn, "LocalLinks") ? ReadGenericTable(conn, "LocalLinks") : [],
            AnalysisHistoryItems: TableExists(conn, "AnalysisHistoryItems") ? ReadGenericTable(conn, "AnalysisHistoryItems") : [],
            BandwidthSamples: TableExists(conn, "BandwidthSamples") ? ReadGenericTable(conn, "BandwidthSamples") : [],
            MissingArticleEvents: TableExists(conn, "MissingArticleEvents") ? ReadGenericTable(conn, "MissingArticleEvents") : [],
            MissingArticleSummaries: TableExists(conn, "MissingArticleSummaries") ? ReadGenericTable(conn, "MissingArticleSummaries") : [],
            NzbProviderStats: TableExists(conn, "NzbProviderStats") ? ReadGenericTable(conn, "NzbProviderStats") : [],
            ProviderBenchmarkResults: TableExists(conn, "ProviderBenchmarkResults") ? ReadGenericTable(conn, "ProviderBenchmarkResults") : []);
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public static IReadOnlyList<IncompatibleTableRow> ReadGenericTable(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{tableName}\"";
        using var reader = cmd.ExecuteReader();
        var result = new List<IncompatibleTableRow>();
        while (reader.Read())
        {
            var columns = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(new IncompatibleTableRow(columns));
        }
        return result;
    }

    private static IReadOnlyList<SourceDavItem> ReadDavItems(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, Path, ReleaseDate,
                   LastHealthCheck, NextHealthCheck, MediaInfo, IsCorrupted, CorruptionReason, HistoryItemId
            FROM DavItems
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceDavItem>();
        while (reader.Read())
        {
            result.Add(new SourceDavItem(
                Id: reader.GetGuid(0),
                IdPrefix: reader.GetString(1),
                CreatedAtUnixSeconds: ToUnixSeconds(reader, 2),
                ParentId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                Name: reader.GetString(4),
                FileSize: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Type: reader.GetInt32(6),
                Path: reader.GetString(7),
                ReleaseDateUnixSeconds: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                LastHealthCheckUnixSeconds: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                NextHealthCheckUnixSeconds: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                MediaInfo: reader.IsDBNull(11) ? null : reader.GetString(11),
                IsCorrupted: !reader.IsDBNull(12) && reader.GetBoolean(12),
                CorruptionReason: reader.IsDBNull(13) ? null : reader.GetString(13),
                HistoryItemId: reader.IsDBNull(14) ? null : reader.GetGuid(14)));
        }
        return result;
    }

    private static IReadOnlyList<SourceDavNzbFile> ReadDavNzbFiles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, SegmentIds, SegmentFallbacks FROM DavNzbFiles";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceDavNzbFile>();
        while (reader.Read())
        {
            var segmentIdsJson = SourceBlobCodec.Decode(reader.GetString(1));
            var segmentIds = JsonSerializer.Deserialize<string[]>(segmentIdsJson) ?? [];
            Dictionary<int, string[]>? fallbacks = null;
            if (!reader.IsDBNull(2))
            {
                var fallbacksJson = SourceBlobCodec.Decode(reader.GetString(2));
                fallbacks = JsonSerializer.Deserialize<Dictionary<int, string[]>>(fallbacksJson);
            }
            result.Add(new SourceDavNzbFile(reader.GetGuid(0), segmentIds, fallbacks));
        }
        return result;
    }

    private static IReadOnlyList<SourceDavMultipartFile> ReadDavMultipartFiles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Metadata FROM DavMultipartFiles";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceDavMultipartFile>();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            var json = SourceBlobCodec.Decode(reader.GetString(1));
            var meta = JsonSerializer.Deserialize<SourceMultipartMetaDto>(json)
                       ?? throw new InvalidOperationException($"DavMultipartFiles.Id={id}: could not parse Metadata JSON.");

            var fileParts = (meta.FileParts ?? []).Select(fp => new SourceSegmentFilePart(
                fp.SegmentIds ?? [],
                fp.SegmentIdByteRange?.StartInclusive ?? 0,
                fp.SegmentIdByteRange?.EndExclusive ?? 0,
                fp.FilePartByteRange?.StartInclusive ?? 0,
                fp.FilePartByteRange?.EndExclusive ?? 0,
                fp.SegmentFallbacks)).ToArray();

            result.Add(new SourceDavMultipartFile(
                id,
                meta.AesParams == null ? null : new SourceAesParams(meta.AesParams.DecodedSize, meta.AesParams.Iv ?? [], meta.AesParams.Key ?? []),
                meta.ObfuscationKey,
                fileParts,
                RawMetadataJson: json));
        }
        return result;
    }

    /// <summary>
    /// Streaming twin of <see cref="ReadDavMultipartFiles"/>: yields one row at a time instead
    /// of building the full list, for callers (StreamingMigrator) that process and discard each
    /// row immediately rather than needing random access to the whole table. Real-world source
    /// databases can carry thousands of these rows with non-trivial per-row JSON, so holding the
    /// full table in memory alongside everything else in the pipeline is what causes the OOM this
    /// streaming path exists to avoid.
    /// </summary>
    public static IEnumerable<SourceDavMultipartFile> StreamDavMultipartFiles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Metadata FROM DavMultipartFiles";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            var json = SourceBlobCodec.Decode(reader.GetString(1));
            var meta = JsonSerializer.Deserialize<SourceMultipartMetaDto>(json)
                       ?? throw new InvalidOperationException($"DavMultipartFiles.Id={id}: could not parse Metadata JSON.");

            var fileParts = (meta.FileParts ?? []).Select(fp => new SourceSegmentFilePart(
                fp.SegmentIds ?? [],
                fp.SegmentIdByteRange?.StartInclusive ?? 0,
                fp.SegmentIdByteRange?.EndExclusive ?? 0,
                fp.FilePartByteRange?.StartInclusive ?? 0,
                fp.FilePartByteRange?.EndExclusive ?? 0,
                fp.SegmentFallbacks)).ToArray();

            yield return new SourceDavMultipartFile(
                id,
                meta.AesParams == null ? null : new SourceAesParams(meta.AesParams.DecodedSize, meta.AesParams.Iv ?? [], meta.AesParams.Key ?? []),
                meta.ObfuscationKey,
                fileParts,
                RawMetadataJson: json);
        }
    }

    /// <summary>Streaming twin of <see cref="ReadDavRarFiles"/> - see StreamDavMultipartFiles.</summary>
    public static IEnumerable<SourceDavRarFile> StreamDavRarFiles(SqliteConnection conn)
    {
        if (!TableExists(conn, "DavRarFiles")) yield break;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, RarParts FROM DavRarFiles";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            var json = SourceBlobCodec.Decode(reader.GetString(1));
            var parts = JsonSerializer.Deserialize<SourceRarPartDto[]>(json) ?? [];
            var rarParts = parts.Select(p => new SourceDavRarPart(
                p.SegmentIds ?? [], p.PartSize, p.Offset, p.ByteCount, p.ObfuscationKey)).ToArray();
            yield return new SourceDavRarFile(id, rarParts);
        }
    }

    /// <summary>Streaming twin of <see cref="ReadDavNzbFiles"/> - see StreamDavMultipartFiles.</summary>
    public static IEnumerable<SourceDavNzbFile> StreamDavNzbFiles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, SegmentIds, SegmentFallbacks FROM DavNzbFiles";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var segmentIdsJson = SourceBlobCodec.Decode(reader.GetString(1));
            var segmentIds = JsonSerializer.Deserialize<string[]>(segmentIdsJson) ?? [];
            Dictionary<int, string[]>? fallbacks = null;
            if (!reader.IsDBNull(2))
            {
                var fallbacksJson = SourceBlobCodec.Decode(reader.GetString(2));
                fallbacks = JsonSerializer.Deserialize<Dictionary<int, string[]>>(fallbacksJson);
            }
            yield return new SourceDavNzbFile(reader.GetGuid(0), segmentIds, fallbacks);
        }
    }

    /// <summary>Streaming twin of <see cref="ReadQueueNzbContents"/> - see StreamDavMultipartFiles.</summary>
    public static IEnumerable<SourceQueueNzbContents> StreamQueueNzbContents(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, NzbContents FROM QueueNzbContents";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return new SourceQueueNzbContents(reader.GetGuid(0), SourceBlobCodec.Decode(reader.GetString(1)));
    }

    /// <summary>Streaming twin of <see cref="ReadHistoryItems"/> - see StreamDavMultipartFiles.</summary>
    public static IEnumerable<SourceHistoryItem> StreamHistoryItems(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, CompletedAt, FileName, JobName, Category, DownloadStatus,
                   TotalSegmentBytes, DownloadTimeSeconds, FailMessage, DownloadDirId, IsHidden,
                   HiddenAt, NzbContents, FailureReason, IsImported, IsArchived, ArchivedAt
            FROM HistoryItems
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new SourceHistoryItem(
                Id: reader.GetGuid(0),
                CreatedAtUnixSeconds: ToUnixSeconds(reader, 1),
                CompletedAtUnixSeconds: ToUnixSeconds(reader, 2),
                FileName: reader.GetString(3),
                JobName: reader.GetString(4),
                Category: reader.GetString(5),
                DownloadStatus: reader.GetInt32(6),
                TotalSegmentBytes: reader.GetInt64(7),
                DownloadTimeSeconds: reader.GetInt32(8),
                FailMessage: reader.IsDBNull(9) ? null : reader.GetString(9),
                DownloadDirId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
                IsHidden: !reader.IsDBNull(11) && reader.GetBoolean(11),
                HiddenAtUnixSeconds: reader.IsDBNull(12) ? null : ToUnixSecondsValue(reader.GetDateTime(12)),
                NzbContents: reader.IsDBNull(13) ? null : SourceBlobCodec.Decode(reader.GetString(13)),
                FailureReason: reader.IsDBNull(14) ? null : reader.GetString(14),
                IsImported: !reader.IsDBNull(15) && reader.GetBoolean(15),
                IsArchived: !reader.IsDBNull(16) && reader.GetBoolean(16),
                ArchivedAtUnixSeconds: reader.IsDBNull(17) ? null : ToUnixSecondsValue(reader.GetDateTime(17)));
        }
    }

    /// <summary>
    /// Streaming twin of <see cref="ReadGenericTable"/> - yields one row at a time instead of
    /// building the full list. Used for the wholly-incompatible tables (AnalysisHistoryItems,
    /// BandwidthSamples, etc.) that only ever get archived, never written to the target DB - real
    /// databases can carry many thousands of these rows.
    /// </summary>
    public static IEnumerable<IncompatibleTableRow> StreamGenericTable(SqliteConnection conn, string tableName)
    {
        if (!TableExists(conn, tableName)) yield break;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{tableName}\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var columns = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return new IncompatibleTableRow(columns);
        }
    }

    private static IReadOnlyList<SourceDavRarFile> ReadDavRarFiles(SqliteConnection conn)
    {
        if (!TableExists(conn, "DavRarFiles")) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, RarParts FROM DavRarFiles";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceDavRarFile>();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            var json = SourceBlobCodec.Decode(reader.GetString(1));
            var parts = JsonSerializer.Deserialize<SourceRarPartDto[]>(json) ?? [];
            var rarParts = parts.Select(p => new SourceDavRarPart(
                p.SegmentIds ?? [], p.PartSize, p.Offset, p.ByteCount, p.ObfuscationKey)).ToArray();
            result.Add(new SourceDavRarFile(id, rarParts));
        }
        return result;
    }

    private static IReadOnlyList<SourceQueueItem> ReadQueueItems(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, FileName, JobName, NzbFileSize, TotalSegmentBytes, Category,
                   Priority, PostProcessing, PauseUntil
            FROM QueueItems
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceQueueItem>();
        while (reader.Read())
        {
            result.Add(new SourceQueueItem(
                reader.GetGuid(0), ToUnixSeconds(reader, 1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.IsDBNull(9) ? null : ToUnixSecondsValue(reader.GetDateTime(9))));
        }
        return result;
    }

    private static IReadOnlyList<SourceQueueNzbContents> ReadQueueNzbContents(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, NzbContents FROM QueueNzbContents";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceQueueNzbContents>();
        while (reader.Read())
            result.Add(new SourceQueueNzbContents(reader.GetGuid(0), SourceBlobCodec.Decode(reader.GetString(1))));
        return result;
    }

    private static IReadOnlyList<SourceHistoryItem> ReadHistoryItems(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CreatedAt, CompletedAt, FileName, JobName, Category, DownloadStatus,
                   TotalSegmentBytes, DownloadTimeSeconds, FailMessage, DownloadDirId, IsHidden,
                   HiddenAt, NzbContents, FailureReason, IsImported, IsArchived, ArchivedAt
            FROM HistoryItems
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceHistoryItem>();
        while (reader.Read())
        {
            result.Add(new SourceHistoryItem(
                Id: reader.GetGuid(0),
                CreatedAtUnixSeconds: ToUnixSeconds(reader, 1),
                CompletedAtUnixSeconds: ToUnixSeconds(reader, 2),
                FileName: reader.GetString(3),
                JobName: reader.GetString(4),
                Category: reader.GetString(5),
                DownloadStatus: reader.GetInt32(6),
                TotalSegmentBytes: reader.GetInt64(7),
                DownloadTimeSeconds: reader.GetInt32(8),
                FailMessage: reader.IsDBNull(9) ? null : reader.GetString(9),
                DownloadDirId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
                IsHidden: !reader.IsDBNull(11) && reader.GetBoolean(11),
                HiddenAtUnixSeconds: reader.IsDBNull(12) ? null : ToUnixSecondsValue(reader.GetDateTime(12)),
                NzbContents: reader.IsDBNull(13) ? null : SourceBlobCodec.Decode(reader.GetString(13)),
                FailureReason: reader.IsDBNull(14) ? null : reader.GetString(14),
                IsImported: !reader.IsDBNull(15) && reader.GetBoolean(15),
                IsArchived: !reader.IsDBNull(16) && reader.GetBoolean(16),
                ArchivedAtUnixSeconds: reader.IsDBNull(17) ? null : ToUnixSecondsValue(reader.GetDateTime(17))));
        }
        return result;
    }

    private static IReadOnlyList<SourceConfigItem> ReadConfigItems(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ConfigName, ConfigValue FROM ConfigItems";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceConfigItem>();
        while (reader.Read())
            result.Add(new SourceConfigItem(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static IReadOnlyList<SourceAccount> ReadAccounts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Type, Username, PasswordHash, RandomSalt FROM Accounts ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceAccount>();
        while (reader.Read())
            result.Add(new SourceAccount(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    private static IReadOnlyList<SourceHealthCheckResult> ReadHealthCheckResults(SqliteConnection conn)
    {
        if (!TableExists(conn, "HealthCheckResults")) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, CreatedAt, DavItemId, Path, Result, RepairStatus, Message, Operation FROM HealthCheckResults";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceHealthCheckResult>();
        while (reader.Read())
        {
            result.Add(new SourceHealthCheckResult(
                reader.GetGuid(0), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? "UNKNOWN" : reader.GetString(7)));
        }
        return result;
    }

    private static IReadOnlyList<SourceHealthCheckStat> ReadHealthCheckStats(SqliteConnection conn)
    {
        if (!TableExists(conn, "HealthCheckStats")) return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count FROM HealthCheckStats";
        using var reader = cmd.ExecuteReader();
        var result = new List<SourceHealthCheckStat>();
        while (reader.Read())
        {
            result.Add(new SourceHealthCheckStat(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        }
        return result;
    }

    private static long ToUnixSeconds(SqliteDataReader reader, int ordinal) => ToUnixSecondsValue(reader.GetDateTime(ordinal));
    private static long ToUnixSecondsValue(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private record SourceLongRangeDto(long StartInclusive, long EndExclusive);
    private record SourceAesParamsDto(long DecodedSize, byte[]? Iv, byte[]? Key);
    private record SourceFilePartDto(
        string[]? SegmentIds, SourceLongRangeDto? SegmentIdByteRange, SourceLongRangeDto? FilePartByteRange,
        Dictionary<int, string[]>? SegmentFallbacks, long[]? SegmentSizes);
    private record SourceMultipartMetaDto(SourceAesParamsDto? AesParams, byte[]? ObfuscationKey, SourceFilePartDto[]? FileParts);
    private record SourceRarPartDto(string[]? SegmentIds, long PartSize, long Offset, long ByteCount, byte[]? ObfuscationKey);
}
