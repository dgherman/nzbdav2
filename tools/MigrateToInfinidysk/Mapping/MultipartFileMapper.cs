using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

public static class MultipartFileMapper
{
    public readonly record struct Result(bool IsUnmigratable, TargetMultipartMeta? Meta, string? SkipReason);

    public static Result Map(SourceDavMultipartFile source, bool wasRarSourced)
    {
        if (ObfuscationDetector.IsUnmigratable(source.ObfuscationKey, wasRarSourced))
        {
            var reason = source.ObfuscationKey != null
                ? $"DavMultipartFiles.Id={source.Id}: non-null ObfuscationKey has no infinidysk equivalent " +
                  "(DavMultipartFile.Meta has no ObfuscationKey field); playback would be broken if imported."
                : $"DavMultipartFiles.Id={source.Id}: RAR-sourced with no recorded ObfuscationKey - nzbdav2's " +
                  "RarDeobfuscationStream falls back to content-sniffed default-key auto-detection at playback " +
                  "time, which cannot be reproduced from the database alone.";
            return new Result(true, null, reason);
        }

        var fileParts = source.FileParts.Select(MapFilePart).ToArray();

        var meta = new TargetMultipartMeta(
            AesParams: source.AesParams == null
                ? null
                : new TargetAesParams(source.AesParams.DecodedSize, source.AesParams.Iv, source.AesParams.Key),
            FileParts: fileParts,
            IsLazy: false,
            PathInArchive: null,
            ArchivePassword: null,
            PendingParts: [],
            ExpectedFileSize: null);

        return new Result(false, meta, null);
    }

    private static TargetFilePart MapFilePart(SourceSegmentFilePart part)
    {
        return new TargetFilePart(
            SegmentIds: part.SegmentIds,
            SegmentIdByteRange: new TargetLongRange(part.SegmentIdByteRangeStart, part.SegmentIdByteRangeEnd),
            FilePartByteRange: new TargetLongRange(part.FilePartByteRangeStart, part.FilePartByteRangeEnd),
            // Byte-range/trust provenance can't be reconstructed from a DB-only import; leaving
            // both null/false forces infinidysk to re-derive them via safe header-probed seeking
            // instead of trusting unverified imported geometry (see infinidysk's
            // SegmentByteRangesTrusted doc comment - read-only reference).
            SegmentByteRanges: null,
            SegmentFallbackIds: SegmentFallbackMapper.ToAlignedArray(part.SegmentFallbacks, part.SegmentIds.Length),
            IsSplitAfter: null,
            SegmentByteRangesTrusted: null);
    }

    /// <summary>
    /// Converts a legacy nzbdav2 DavRarFile row (pre-v0.8.0 shape) into the same
    /// SourceDavMultipartFile shape Map() expects, mirroring nzbdav2's own
    /// DavRarFile.ToDavMultipartFileMeta() conversion (backend/Database/Models/DavRarFile.cs).
    /// </summary>
    public static SourceDavMultipartFile FromRarFile(SourceDavRarFile rarFile)
    {
        var obfuscationKey = rarFile.RarParts.Select(p => p.ObfuscationKey).FirstOrDefault(k => k != null);
        var fileParts = rarFile.RarParts.Select(p => new SourceSegmentFilePart(
            SegmentIds: p.SegmentIds,
            SegmentIdByteRangeStart: 0,
            SegmentIdByteRangeEnd: p.PartSize,
            FilePartByteRangeStart: p.Offset,
            FilePartByteRangeEnd: p.Offset + p.ByteCount,
            SegmentFallbacks: null)).ToArray();

        return new SourceDavMultipartFile(rarFile.Id, AesParams: null, obfuscationKey, fileParts);
    }
}
