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

    private const int MaxGlobalRarHeaderConnections = 6;
    private const int MaxRarHeaderConnectionsPerPart = 2;
    private static readonly SemaphoreSlim RarHeaderConnectionSlots = new(MaxGlobalRarHeaderConnections, MaxGlobalRarHeaderConnections);

    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        Log.Information("[RarProcessor] Starting parallel RAR processing for {Count} parts", fileInfos.Count);

        var sortedInfos = fileInfos.OrderBy(f => GetPartNumber(f.FileName)).ToList();

        // Keep RAR header extraction bounded globally. Each active part may use multiple
        // buffered segment workers, so part concurrency must be capped by the shared header
        // connection budget rather than scaling directly with archive part count.
        var partCount = sortedInfos.Count;
        var requestedConcurrency = partCount switch
        {
            > 50 => Math.Min(20, maxConcurrentConnections * 3),  // Large sets: up to 20 concurrent
            > 20 => Math.Min(15, maxConcurrentConnections * 2),  // Medium sets: up to 15 concurrent
            > 10 => Math.Min(10, maxConcurrentConnections),      // Small-medium: up to 10 concurrent
            _ => Math.Max(3, maxConcurrentConnections)           // Small sets: at least 3 concurrent
        };
        var maxConcurrentHeaderParts = Math.Max(1, MaxGlobalRarHeaderConnections / MaxRarHeaderConnectionsPerPart);
        var concurrency = Math.Min(requestedConcurrency, maxConcurrentHeaderParts);
        Log.Debug("[RarProcessor] Processing {Count} parts with concurrency {Concurrency} (requested {RequestedConcurrency}, global header connection cap {HeaderConnectionCap})",
            partCount, concurrency, requestedConcurrency, MaxGlobalRarHeaderConnections);

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
        var connectionCount = GetRarHeaderConnectionCount(segments.Length);
        using var headerConnectionLease = await AcquireRarHeaderConnectionSlotsAsync(connectionCount, headerCts.Token).ConfigureAwait(false);

        // Use FAST stream that trusts the file size to avoid slow segment re-scans
        await using var stream = await GetFastNzbFileStream(fileInfo, segments, connectionCount, headerCts.Token).ConfigureAwait(false);
        
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
                var computed = await usenet.AnalyzeNzbAsync(partIds, MaxRarHeaderConnectionsPerPart, null, ct, useSmartAnalysis: true).ConfigureAwait(false);
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
    /// Number of connections to use per RAR part for header reading.
    /// Using a small number of buffered connections speeds up header extraction while the
    /// global RAR header slot limiter prevents part-count multiplication.
    /// </summary>
    private static int GetRarHeaderConnectionCount(int segmentCount)
    {
        return Math.Max(1, Math.Min(MaxRarHeaderConnectionsPerPart, segmentCount));
    }

    private async Task<NzbFileStream> GetFastNzbFileStream(
        GetFileInfosStep.FileInfo fileInfo,
        string[] segments,
        int connectionCount,
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

        // Create a QueueRarProcessing context so NzbFileStream allows buffered streaming
        // (Queue and QueueAnalysis contexts disable buffering, but QueueRarProcessing does not)
        var parentContext = ct.GetContext<ConnectionUsageContext>();
        var usageContext = parentContext.DetailsObject != null
            ? new ConnectionUsageContext(ConnectionUsageType.QueueRarProcessing, parentContext.DetailsObject)
            : new ConnectionUsageContext(ConnectionUsageType.QueueRarProcessing, parentContext.Details);

        // If we have exact segment sizes, use the standard stream with buffering
        // otherwise use the fast stream that trusts the total size
        return segmentSizes != null
            ? usenet.GetFileStream(segments, filesize.Value, connectionCount, usageContext, useBufferedStreaming: true, bufferSize: connectionCount * 3, segmentSizes: segmentSizes)
            : usenet.GetFileStream(segments, filesize.Value, connectionCount, usageContext, useBufferedStreaming: true, bufferSize: connectionCount * 3);
    }

    private static async Task<RarHeaderConnectionLease> AcquireRarHeaderConnectionSlotsAsync(int count, CancellationToken ct)
    {
        var acquired = 0;
        try
        {
            while (acquired < count)
            {
                await RarHeaderConnectionSlots.WaitAsync(ct).ConfigureAwait(false);
                acquired++;
            }

            return new RarHeaderConnectionLease(acquired);
        }
        catch
        {
            if (acquired > 0) RarHeaderConnectionSlots.Release(acquired);
            throw;
        }
    }

    private sealed class RarHeaderConnectionLease(int count) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && count > 0)
            {
                RarHeaderConnectionSlots.Release(count);
            }
        }
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

        /// <summary>Decoded per-segment sizes of the volume's segments (sum to PartSize), or null if unknown.</summary>
        public long[]? SegmentSizes { get; init; }
    }
}
