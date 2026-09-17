namespace NzbWebDAV.MigrateToInfinidysk.Model;

public record TargetDavItem(
    Guid Id, string IdPrefix, long CreatedAtUnixSeconds, Guid? ParentId, string Name, long? FileSize,
    int Type, int SubType, string Path, long? ReleaseDateUnixSeconds, long? LastHealthCheckUnixSeconds,
    long? NextHealthCheckUnixSeconds, bool HealthRepairPending, Guid? HistoryItemId);

public record TargetDavNzbFile(Guid Id, string SegmentIdsJson);

public record TargetDavMultipartFile(Guid Id, string MetadataJson);

public record TargetQueueItem(
    Guid Id, long CreatedAtUnixSeconds, long SortOrder, string FileName, string JobName, long NzbFileSize,
    long TotalSegmentBytes, string Category, int Priority, int PostProcessing, long? PauseUntilUnixSeconds);

public record TargetQueueNzbContents(Guid Id, string NzbContents);

public record TargetHistoryItem(
    Guid Id, long CreatedAtUnixSeconds, string Category, long DownloadStatusValue, int DownloadTimeSeconds,
    string? FailMessage, string FileName, string JobName, long TotalSegmentBytes, Guid? DownloadDirId);

public record TargetConfigItem(string ConfigName, string ConfigValue);

public record TargetAccount(int Type, string Username, string PasswordHash, string RandomSalt);

public record TargetHealthCheckResult(
    Guid Id, long CreatedAtUnixSeconds, Guid DavItemId, string Path, int Result, int RepairStatus, string? Message);

public record TargetHealthCheckStat(
    long DateStartInclusiveUnixSeconds, long DateEndExclusiveUnixSeconds, int Result, int RepairStatus, int Count);
