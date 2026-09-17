namespace NzbWebDAV.MigrateToInfinidysk.Model;

// Plain records mirroring nzbdav2's backend/Database/Models/*.cs shapes, populated by
// SqliteSourceReader from a nzbdav2 db.sqlite. Kept separate from infinidysk's own types -
// this tool never references the infinidysk source tree.

public record SourceDavItem(
    Guid Id,
    string IdPrefix,
    long CreatedAtUnixSeconds,
    Guid? ParentId,
    string Name,
    long? FileSize,
    int Type, // DavItem.ItemType: 1=Directory 2=SymlinkRoot 3=NzbFile 4=RarFile 5=IdsRoot 6=MultipartFile
    string Path,
    long? ReleaseDateUnixSeconds,
    long? LastHealthCheckUnixSeconds,
    long? NextHealthCheckUnixSeconds,
    string? MediaInfo,
    bool IsCorrupted,
    string? CorruptionReason,
    Guid? HistoryItemId);

public record SourceSegmentFilePart(
    string[] SegmentIds,
    long SegmentIdByteRangeStart,
    long SegmentIdByteRangeEnd,
    long FilePartByteRangeStart,
    long FilePartByteRangeEnd,
    Dictionary<int, string[]>? SegmentFallbacks);

public record SourceAesParams(long DecodedSize, byte[] Iv, byte[] Key);

public record SourceDavNzbFile(Guid Id, string[] SegmentIds, Dictionary<int, string[]>? SegmentFallbacks);

public record SourceDavMultipartFile(
    Guid Id,
    SourceAesParams? AesParams,
    byte[]? ObfuscationKey,
    SourceSegmentFilePart[] FileParts);

public record SourceDavRarPart(
    string[] SegmentIds,
    long PartSize,
    long Offset,
    long ByteCount,
    byte[]? ObfuscationKey);

public record SourceDavRarFile(Guid Id, SourceDavRarPart[] RarParts);

public record SourceQueueItem(
    Guid Id,
    long CreatedAtUnixSeconds,
    string FileName,
    string JobName,
    long NzbFileSize,
    long TotalSegmentBytes,
    string Category,
    int Priority,
    int PostProcessing,
    long? PauseUntilUnixSeconds);

public record SourceQueueNzbContents(Guid Id, string NzbContents);

public record SourceHistoryItem(
    Guid Id,
    long CreatedAtUnixSeconds,
    long CompletedAtUnixSeconds,
    string FileName,
    string JobName,
    string Category,
    int DownloadStatus,
    long TotalSegmentBytes,
    int DownloadTimeSeconds,
    string? FailMessage,
    Guid? DownloadDirId,
    bool IsHidden,
    long? HiddenAtUnixSeconds,
    string? NzbContents,
    string? FailureReason,
    bool IsImported,
    bool IsArchived,
    long? ArchivedAtUnixSeconds);

public record SourceConfigItem(string ConfigName, string ConfigValue);

public record SourceAccount(int Type, string Username, string PasswordHash, string RandomSalt);

public record SourceHealthCheckResult(
    Guid Id,
    long CreatedAtUnixSeconds,
    Guid DavItemId,
    string Path,
    int Result,
    int RepairStatus,
    string? Message,
    string Operation);

public record SourceHealthCheckStat(
    long DateStartInclusiveUnixSeconds,
    long DateEndExclusiveUnixSeconds,
    int Result,
    int RepairStatus,
    int Count);

// INCOMPATIBLE tables: no infinidysk equivalent at all (LocalLinks, AnalysisHistoryItems,
// BandwidthSamples, MissingArticleEvents, MissingArticleSummaries, NzbProviderStats,
// ProviderBenchmarkResults). Rather than mirror each one's exact column set here (and risk
// silently dropping a column when nzbdav2's schema evolves), these are read generically as
// column-name -> value rows and archived to the JSON sidecar verbatim. See
// SqliteSourceReader.ReadGenericTable and Archive/IncompatibleTableSnapshot.
public record IncompatibleTableRow(IReadOnlyDictionary<string, object?> Columns);
