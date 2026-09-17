namespace NzbWebDAV.MigrateToInfinidysk.Model;

// JSON-shape mirrors of infinidysk's DavMultipartFile.Meta/FilePart/PendingPart and
// AesParams/LongRange (infinidysk/backend/Database/Models/DavMultipartFile.cs,
// infinidysk/backend/Models/AesParams.cs, infinidysk/backend/Models/LongRange.cs - read-only
// reference). infinidysk stores these as plain System.Text.Json output in its
// DavMultipartFiles.Metadata / DavRarFiles.RarParts TEXT columns (see
// DavDatabaseContext.OnModelCreating: `JsonSerializer.Serialize(v, (JsonSerializerOptions?)null)`
// with default naming, i.e. PascalCase property names as declared). These types exist purely
// to produce byte-for-byte-compatible JSON text via the same default serializer settings -
// they are not a reference to infinidysk's assembly.

public record TargetLongRange(long StartInclusive, long EndExclusive);

public record TargetAesParams(long DecodedSize, byte[] Iv, byte[] Key);

public record TargetFilePart(
    string[] SegmentIds,
    TargetLongRange SegmentIdByteRange,
    TargetLongRange FilePartByteRange,
    TargetLongRange[]? SegmentByteRanges,
    string[][]? SegmentFallbackIds,
    bool? IsSplitAfter,
    bool? SegmentByteRangesTrusted);

public record TargetPendingPart(
    string[] SegmentIds,
    TargetLongRange SegmentIdByteRange,
    long EstimatedDataSize,
    string[][]? SegmentFallbackIds);

public record TargetMultipartMeta(
    TargetAesParams? AesParams,
    TargetFilePart[] FileParts,
    bool IsLazy,
    string? PathInArchive,
    string? ArchivePassword,
    TargetPendingPart[] PendingParts,
    long? ExpectedFileSize);
