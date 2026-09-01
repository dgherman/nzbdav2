using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using NzbWebDAV.Database.Models;
using Usenet.Nzb;

namespace NzbWebDAV.Queue.FileProcessors;

public class RarProcessor(
    List<GetFileInfosStep.FileInfo> fileInfos,
    UsenetStreamingClient usenet,
    string? password,
    CancellationToken ct,
    int headerConnectionBudget = 1
) : BaseProcessor
{
    /// <summary>
    /// Header parsing reads unbuffered, so the header read itself occupies a single connection.
    /// </summary>
    private const int HeaderConnectionsPerPart = 1;

    /// <summary>
    /// Fan-out of the per-volume segment-size analysis that follows the header read.
    /// </summary>
    private const int SegmentAnalysisConnectionsPerPart = 2;

    /// <summary>
    /// Most connections one volume holds at any moment. The header read and the analysis pass run
    /// one after the other inside <see cref="ProcessPartAsync"/>, so the peak is the larger of the
    /// two rather than their sum — but it is the peak the connection budget has to be divided by.
    /// </summary>
    private const int PeakConnectionsPerPart =
        HeaderConnectionsPerPart > SegmentAnalysisConnectionsPerPart
            ? HeaderConnectionsPerPart
            : SegmentAnalysisConnectionsPerPart;

    /// <summary>
    /// Assumed segment size when a volume declares none, used only to size the memory budget.
    /// Typical usenet segment sizes sit around 700-750 KB.
    /// </summary>
    private const long AssumedSegmentBytes = 750 * 1024;

    /// <summary>
    /// Articles in flight per volume while its headers are parsed: the one at the front of the
    /// volume, plus the one at its end-archive header across the seek.
    /// </summary>
    private const int InFlightArticlesPerPart = 2;

    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        var sortedInfos = fileInfos.OrderBy(f => GetPartNumber(f.FileName)).ToList();
        var partCount = sortedInfos.Count;

        // Two independent limits, whichever is tighter.
        //
        // Before v0.12.2 this was a fixed budget of 6 connections at 2 per volume — 3 volumes at
        // a time, process-wide — because header reads were buffered and each volume prefetched
        // several segments. That cost was self-inflicted: header parsing reads a few hundred
        // bytes at the front of a volume and then seeks, so a prefetch window is wasted work as
        // well as wasted heap. Reading unbuffered drops a volume to roughly one article, which
        // makes the connection budget the binding limit rather than memory, matching upstream.
        // The old cap was worth ~240s on a 71-volume archive (71 / 3 * ~10s), long enough for a
        // Stremio addon to give up waiting for the import.
        var residentBytesPerPart =
            AverageSegmentBytes(sortedInfos) * InFlightArticlesPerPart * MemoryBudget.ResidentBytesPerDataByte;
        var memoryLimit = MemoryBudget.MaxConcurrentRarHeaderParts(
            MemoryBudget.HeapLimitBytes, residentBytesPerPart);
        var connectionLimit = Math.Max(1, headerConnectionBudget / PeakConnectionsPerPart);
        var concurrency = Math.Clamp(Math.Min(connectionLimit, memoryLimit), 1, Math.Max(1, partCount));

        Log.Information(
            "[RarProcessor] Parsing {Count} RAR volume headers, {Concurrency} at a time " +
            "(connection budget {ConnectionLimit}, heap affords {MemoryLimit} at ~{PerPartKB}KB each)",
            partCount, concurrency, connectionLimit, memoryLimit, residentBytesPerPart / 1024);

        var tasks = sortedInfos
            .Select(async fileInfo =>
            {
                if (fileInfo.MissingFirstSegment)
                {
                    Log.Warning("[RarProcessor] Skipping part {FileName} because the first segment is missing.", fileInfo.FileName);
                    return new List<StoredFileSegment>();
                }

                try
                {
                    return await ProcessPartAsync(fileInfo).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[RarProcessor] Failed to process part {FileName}: {Message}", fileInfo.FileName, ex.Message);
                    return new List<StoredFileSegment>();
                }
            })
            .WithConcurrencyAsync(concurrency);

        var allSegments = new List<StoredFileSegment>();
        await foreach (var segments in tasks.ConfigureAwait(false))
        {
            allSegments.AddRange(segments);
        }

        if (allSegments.Count == 0)
        {
            Log.Error("[RarProcessor] No files found in any of the {Count} RAR parts", fileInfos.Count);
            return null;
        }

        return new Result()
        {
            StoredFileSegments = allSegments.ToArray(),
        };
    }

    private async Task<List<StoredFileSegment>> ProcessPartAsync(GetFileInfosStep.FileInfo fileInfo)
    {
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(60));

        var segments = fileInfo.NzbFile.GetSegmentIds();

        // Use FAST stream that trusts the file size to avoid slow segment re-scans
        await using var stream = await GetFastNzbFileStream(fileInfo, segments, headerCts.Token).ConfigureAwait(false);
        
        if (fileInfo.MagicOffset > 0)
        {
            stream.Seek(fileInfo.MagicOffset, SeekOrigin.Begin);
        }

        var headers = await RarUtil.GetRarHeadersAsync(stream, password, headerCts.Token).ConfigureAwait(false);

        var archiveName = GetArchiveName(fileInfo);

        // Try to get volume number from RAR headers (more reliable than filename parsing)
        var volumeNumber = GetVolumeNumberFromHeaders(headers);
        var partNumber = volumeNumber ?? GetPartNumber(fileInfo.FileName);

        if (volumeNumber.HasValue)
        {
            Log.Debug("[RarProcessor] Using RAR header volume number {VolumeNumber} for {FileName}", volumeNumber.Value, fileInfo.FileName);
        }

        var offset = Math.Max(0, fileInfo.MagicOffset);

        // Precompute exact decoded per-segment sizes for this volume so streaming gets fast, precise
        // seeking. Uniform volumes resolve in ~3 yEnc-header fetches; on any failure leave null and
        // let the lazy stream-time path handle it.
        long[]? partSegmentSizes = null;
        try
        {
            var partIds = fileInfo.NzbFile.GetSegmentIds();
            if (partIds.Length > 0)
            {
                var computed = await usenet.AnalyzeNzbAsync(partIds, SegmentAnalysisConnectionsPerPart, null, ct, useSmartAnalysis: true).ConfigureAwait(false);
                if (SegmentOffsetTable.TryBuild(computed, partIds.Length, stream.Length, out _))
                    partSegmentSizes = computed;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RarProcessor] Could not precompute segment sizes for {FileName}; lazy path will handle it.", fileInfo.FileName);
        }

        var results = new List<StoredFileSegment>();
        foreach (var x in headers.Where(h => h.HeaderType == HeaderType.File))
        {
            byte[]? obfuscationKey = null;
            
            // If the file is "Stored" (uncompressed), check for obfuscation
            if (x.GetCompressionMethod() == 0)
            {
                try
                {
                    // Seek to the start of file data
                    stream.Position = x.GetDataStartPosition() + offset;
                    var sigBuffer = new byte[4];
                    var sigRead = await stream.ReadAsync(sigBuffer, 0, 4, ct).ConfigureAwait(false);

                    if (sigRead == 4 && sigBuffer[0] == 0xAA && sigBuffer[1] == 0x04 && sigBuffer[2] == 0x1D && sigBuffer[3] == 0x6D)
                    {
                        // Use the standard obfuscation key (same as used by nzbget/unrar)
                        obfuscationKey = new byte[] { 0xB0, 0x41, 0xC2, 0xCE };
                        var internalName = x.GetFileName();
                        Log.Information("[RarProcessor] Detected obfuscated Stored file: {InternalName}. Using standard XOR key", internalName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[RarProcessor] Failed to check for obfuscation signature at offset {Offset}", x.GetDataStartPosition() + offset);
                }
            }

            var uncompressedSize = x.GetUncompressedSize();

            results.Add(new StoredFileSegment()
            {
                NzbFile = fileInfo.NzbFile,
                PartSize = stream.Length,
                ArchiveName = archiveName,
                PartNumber = partNumber,
                FileUncompressedSize = uncompressedSize,
                IsUncompressedSizeUnknown = uncompressedSize is <= 0 or long.MaxValue,
                PathWithinArchive = x.GetFileName(),
                ByteRangeWithinPart = LongRange.FromStartAndSize(
                    x.GetDataStartPosition() + offset,
                    x.GetAdditionalDataSize()
                ),
                AesParams = x.GetAesParams(password),
                ObfuscationKey = obfuscationKey,
                ReleaseDate = fileInfo.ReleaseDate,
                SegmentSizes = partSegmentSizes,
            });
        }

        return results;
    }

    private string GetArchiveName(GetFileInfosStep.FileInfo fileInfo)
    {
        return FilenameUtil.GetMultipartBaseName(fileInfo.FileName);
    }

    private static int GetPartNumber(string filename)
    {
        // handle the `.partXXX.rar` format
        var partMatch = Regex.Match(filename, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (partMatch.Success) return int.Parse(partMatch.Groups[1].Value);

        // handle the `.rXXX` format
        var rMatch = Regex.Match(filename, @"\.r(\d+)$", RegexOptions.IgnoreCase);
        if (rMatch.Success) return int.Parse(rMatch.Groups[1].Value);

        // handle the `.rar` format.
        if (filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return -1;

        // handle `.001` etc
        var numericMatch = Regex.Match(filename, @"\.(\d+)$", RegexOptions.IgnoreCase);
        if (numericMatch.Success) return int.Parse(numericMatch.Groups[1].Value);

        return 0;
    }

    /// <summary>
    /// Extracts the volume number from RAR headers.
    /// Checks EndArchiveHeader first (RAR4), then ArchiveHeader (RAR5).
    /// </summary>
    private static int? GetVolumeNumberFromHeaders(List<IRarHeader> headers)
    {
        // Try EndArchiveHeader first (has VolumeNumber for RAR4 multi-volume)
        var endArchiveHeader = headers.FirstOrDefault(h => h.HeaderType == HeaderType.EndArchive);
        if (endArchiveHeader != null)
        {
            var volumeNum = endArchiveHeader.GetVolumeNumber();
            if (volumeNum.HasValue)
            {
                return volumeNum.Value;
            }
        }

        // Try ArchiveHeader (RAR5 has VolumeNumber in archive header)
        var archiveHeader = headers.FirstOrDefault(h => h.HeaderType == HeaderType.Archive);
        if (archiveHeader != null)
        {
            var volumeNum = archiveHeader.GetVolumeNumber();
            if (volumeNum.HasValue)
            {
                return volumeNum.Value;
            }

            // For RAR4, check if this is the first volume using IsFirstVolume flag
            try
            {
                var isFirst = archiveHeader.GetIsFirstVolume();
                if (isFirst)
                {
                    return 0; // First volume
                }
            }
            catch
            {
                // IsFirstVolume may not be available for all header types
            }
        }

        return null;
    }

    /// <summary>
    /// Mean decoded segment size across the volumes, used only to size the memory budget. Falls
    /// back to <see cref="AssumedSegmentBytes"/> for volumes whose size PAR2 did not supply.
    /// </summary>
    private static long AverageSegmentBytes(List<GetFileInfosStep.FileInfo> infos)
    {
        var sizes = infos
            .Select(x =>
            {
                var segmentCount = x.NzbFile.GetSegmentIds().Length;
                if (segmentCount <= 0) return AssumedSegmentBytes;
                var fileSize = x.FileSize ?? (x.SegmentSizes?.Sum() ?? 0);
                return fileSize > 0 ? fileSize / segmentCount : AssumedSegmentBytes;
            })
            .ToList();

        return sizes.Count > 0 ? (long)sizes.Average() : AssumedSegmentBytes;
    }

    private async Task<NzbFileStream> GetFastNzbFileStream(
        GetFileInfosStep.FileInfo fileInfo,
        string[] segments,
        CancellationToken cancellationToken)
    {
        // For RAR processing, we trust the Par2/NZB size if available
        var segmentSizes = fileInfo.SegmentSizes;
        var filesize = fileInfo.FileSize;

        if (segmentSizes != null && filesize == null)
        {
            filesize = segmentSizes.Sum();
        }

        if (filesize == null)
        {
            filesize = await usenet.GetFileSizeAsync(fileInfo.NzbFile, cancellationToken).ConfigureAwait(false);
        }

        // QueueRarProcessing keeps this operation distinguishable from queue downloads in the
        // connection accounting and provider stats.
        var parentContext = ct.GetContext<ConnectionUsageContext>();
        var usageContext = parentContext.DetailsObject != null
            ? new ConnectionUsageContext(ConnectionUsageType.QueueRarProcessing, parentContext.DetailsObject)
            : new ConnectionUsageContext(ConnectionUsageType.QueueRarProcessing, parentContext.Details);

        // Unbuffered on purpose. Reading headers touches the front of the volume and then seeks
        // to the end-archive header; NzbFileStream.Seek only moves a cursor, so a prefetch window
        // would fetch segments nothing ever reads and hold them against the heap. Dropping it is
        // what lets many volumes parse at once instead of three.
        //
        // Seeking has to be exact. When exact per-segment sizes are known (passed in), the stream
        // seeks straight through the offset table. When they are NOT known we pass null and let
        // NzbFileStream locate the target segment by interpolation-searching the real yEnc part
        // headers -- a few HEAD fetches per seek, and correct. Fabricating a uniform size table
        // (the old GetFastFileStream path) is what issue #28 was: fileSize/segmentCount drifts
        // hundreds of KB from the real article sizes across a volume, so the seek to a -hp
        // archive's file/end header near EOF lands in the wrong segment and SharpCompress walks
        // ciphertext -- "malformed vint", "Unknown Rar Header: N", "End of stream reached".
        return usenet.GetFileStream(
            segments, filesize.Value, HeaderConnectionsPerPart, usageContext,
            useBufferedStreaming: false, segmentSizes: segmentSizes);
    }

    public new class Result : BaseProcessor.Result
    {
        public required StoredFileSegment[] StoredFileSegments { get; init; }
    }

    public class StoredFileSegment
    {
        public required NzbFile NzbFile { get; init; }
        public required long PartSize { get; init; }
        public required string ArchiveName { get; init; }
        public required int PartNumber { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public required string PathWithinArchive { get; init; }
        public required LongRange ByteRangeWithinPart { get; init; }
        public required AesParams? AesParams { get; init; }
        public byte[]? ObfuscationKey { get; init; }

        /// <summary>
        /// Size of the whole extracted file as declared by this volume's file header — not the
        /// slice this volume carries. Every volume of a set declares the same value, which is
        /// what lets the aggregator tell a complete volume set from a partial one.
        /// </summary>
        public required long FileUncompressedSize { get; init; }

        /// <summary>
        /// True when the header set RAR5's "unpacked size unknown" flag, which SharpCompress
        /// surfaces as <see cref="long.MaxValue"/>. <see cref="FileUncompressedSize"/> carries
        /// no information in that case.
        /// </summary>
        public required bool IsUncompressedSizeUnknown { get; init; }

        /// <summary>Decoded per-segment sizes of the volume's segments (sum to PartSize), or null if unknown.</summary>
        public long[]? SegmentSizes { get; init; }
    }
}
