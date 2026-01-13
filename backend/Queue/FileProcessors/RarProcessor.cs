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
    int maxConcurrentConnections = 1
) : BaseProcessor
{
    private readonly GetFileInfosStep.FileInfo _primaryFile = fileInfos.OrderBy(f => GetPartNumber(f.FileName)).First();

    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        Log.Information("[RarProcessor] Starting parallel RAR processing for {Count} parts", fileInfos.Count);

        var sortedInfos = fileInfos.OrderBy(f => GetPartNumber(f.FileName)).ToList();
        
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
            .WithConcurrencyAsync(Math.Max(1, maxConcurrentConnections / 2)); // Use moderate concurrency for the parallel parts

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
        // Use FAST stream that trusts the file size to avoid slow segment re-scans
        await using var stream = await GetFastNzbFileStream(fileInfo).ConfigureAwait(false);
        
        if (fileInfo.MagicOffset > 0)
        {
            stream.Seek(fileInfo.MagicOffset, SeekOrigin.Begin);
        }

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(60));

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

            results.Add(new StoredFileSegment()
            {
                NzbFile = fileInfo.NzbFile,
                PartSize = stream.Length,
                ArchiveName = archiveName,
                PartNumber = partNumber,
                PathWithinArchive = x.GetFileName(),
                ByteRangeWithinPart = LongRange.FromStartAndSize(
                    x.GetDataStartPosition() + offset,
                    x.GetAdditionalDataSize()
                ),
                AesParams = x.GetAesParams(password),
                ObfuscationKey = obfuscationKey,
                ReleaseDate = fileInfo.ReleaseDate,
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

    private async Task<NzbFileStream> GetFastNzbFileStream(GetFileInfosStep.FileInfo fileInfo)
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
            filesize = await usenet.GetFileSizeAsync(fileInfo.NzbFile, ct).ConfigureAwait(false);
        }
        
        // Header reading only needs 1 connection usually
        var usageContext = ct.GetContext<ConnectionUsageContext>();
        
        // If we have exact segment sizes, use the standard stream (it will be fast)
        // otherwise use the fast stream that trusts the total size
        return segmentSizes != null 
            ? usenet.GetFileStream(fileInfo.NzbFile.GetSegmentIds(), filesize.Value, 1, usageContext, useBufferedStreaming: false, segmentSizes: segmentSizes)
            : usenet.GetFastFileStream(fileInfo.NzbFile.GetSegmentIds(), filesize.Value, 1, usageContext);
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
    }
}
