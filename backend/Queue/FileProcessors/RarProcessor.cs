using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar.Headers;
using Usenet.Nzb;

namespace NzbWebDAV.Queue.FileProcessors;

public class RarProcessor(
    GetFileInfosStep.FileInfo fileInfo,
    UsenetStreamingClient usenet,
    string? password,
    CancellationToken ct,
    int maxConcurrentConnections = 1
) : BaseProcessor
{
    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        Log.Debug($"[RarProcessor] Initializing stream for {fileInfo.FileName}");
        await using var stream = await GetNzbFileStream().ConfigureAwait(false);

        // Create a linked token source with a timeout for the header parsing operation
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(60));

        Log.Debug($"[RarProcessor] Reading RAR headers for {fileInfo.FileName} (timeout 60s)...");
        List<IRarHeader> headers;
        try
        {
            headers = await RarUtil.GetRarHeadersAsync(stream, password, headerCts.Token).ConfigureAwait(false);
            Log.Debug($"[RarProcessor] Successfully read {headers.Count} RAR headers for {fileInfo.FileName}");
        }
        catch (OperationCanceledException) when (headerCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Log.Error($"[RarProcessor] Timeout reading RAR headers for {fileInfo.FileName}");
            throw new TimeoutException("Timed out while reading RAR headers (limit: 60s). This usually indicates a corrupt or malformed archive.");
        }

        var archiveName = GetArchiveName();
        var partNumber = GetPartNumber(headers);
        return new Result()
        {
            StoredFileSegments = headers
                .Where(x => x.HeaderType == HeaderType.File)
                .Select(x => new StoredFileSegment()
                {
                    NzbFile = fileInfo.NzbFile,
                    PartSize = stream.Length,
                    ArchiveName = archiveName,
                    PartNumber = partNumber,
                    PathWithinArchive = x.GetFileName(),
                    ByteRangeWithinPart = LongRange.FromStartAndSize(
                        x.GetDataStartPosition(),
                        x.GetAdditionalDataSize()
                    ),
                    AesParams = x.GetAesParams(password),
                    ReleaseDate = fileInfo.ReleaseDate,
                }).ToArray(),
        };
    }

    private string GetArchiveName()
    {
        // remove the .rar extension and remove the .partXX if it exists
        var sansExtension = Path.GetFileNameWithoutExtension(fileInfo.FileName);
        sansExtension = Regex.Replace(sansExtension, @"\.part\d+$", "");
        return sansExtension;
    }

    private int GetPartNumber(List<IRarHeader> rarHeaders)
    {
        // read from archive-header if possible
        var partNumberFromHeaders = GetPartNumberFromHeaders(rarHeaders);
        if (partNumberFromHeaders != null) return partNumberFromHeaders!.Value;

        // handle the `.partXXX.rar` format
        var partMatch = Regex.Match(fileInfo.FileName, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (partMatch.Success) return int.Parse(partMatch.Groups[1].Value);

        // handle the `.rXXX` format
        var rMatch = Regex.Match(fileInfo.FileName, @"\.r(\d+)$", RegexOptions.IgnoreCase);
        if (rMatch.Success) return int.Parse(rMatch.Groups[1].Value);

        // handle the `.rar` format.
        if (fileInfo.FileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return -1;

        // we were unable to determine the part number.
        throw new Exception("Could not determine part number for RAR file.");
    }

    private static int? GetPartNumberFromHeaders(List<IRarHeader> headers)
    {
        headers = headers.Where(x => x.HeaderType is HeaderType.Archive or HeaderType.EndArchive).ToList();

        var archiveHeader = headers.FirstOrDefault(x => x.HeaderType is HeaderType.Archive);
        var archiveVolumeNumber = archiveHeader?.GetVolumeNumber();
        if (archiveVolumeNumber != null) return archiveVolumeNumber!.Value;
        if (archiveHeader?.GetIsFirstVolume() == true) return -1;

        var endHeader = headers.FirstOrDefault(x => x.HeaderType == HeaderType.EndArchive);
        var endVolumeNumber = endHeader?.GetVolumeNumber();
        if (endVolumeNumber != null) return endVolumeNumber!.Value;

        return null;
    }

    private async Task<NzbFileStream> GetNzbFileStream()
    {
        var filesize = fileInfo.FileSize ?? await usenet.GetFileSizeAsync(fileInfo.NzbFile, ct).ConfigureAwait(false);
        // Use adaptive concurrency for faster header reading (limited to 10 to avoid over-subscription)
        var concurrency = Math.Min(maxConcurrentConnections, 10);
        var usageContext = ct.GetContext<ConnectionUsageContext>();
        return usenet.GetFileStream(fileInfo.NzbFile.GetSegmentIds(), filesize, concurrency, usageContext);
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
    }
}