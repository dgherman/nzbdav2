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
        Log.Information("[RarProcessor] Starting RAR processing for {FileName}", fileInfo.FileName);

        if (fileInfo.MissingFirstSegment)
        {
            Log.Error("[RarProcessor] Skipping {FileName} because the first segment is missing.", fileInfo.FileName);
            throw new NzbWebDAV.Exceptions.UsenetArticleNotFoundException(fileInfo.NzbFile.Segments.FirstOrDefault()?.MessageId ?? "unknown");
        }

        Log.Debug("[RarProcessor] Initializing stream for {FileName}. FileSize: {FileSize}, Segments: {SegmentCount}",
            fileInfo.FileName, fileInfo.FileSize, fileInfo.NzbFile.Segments.Count);
        await using var stream = await GetNzbFileStream().ConfigureAwait(false);
        Log.Debug("[RarProcessor] Stream initialized. Length: {StreamLength}", stream.Length);

        // Create a linked token source with a timeout for the header parsing operation
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(60));

        Log.Information("[RarProcessor] Reading RAR headers for {FileName} (timeout 60s)...", fileInfo.FileName);
        var headerStartTime = DateTime.UtcNow;
        List<IRarHeader> headers;
        try
        {
            headers = await RarUtil.GetRarHeadersAsync(stream, password, headerCts.Token).ConfigureAwait(false);
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Information("[RarProcessor] Successfully read {HeaderCount} RAR headers for {FileName} in {ElapsedSeconds}s",
                headers.Count, fileInfo.FileName, headerElapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (headerCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error("[RarProcessor] Timeout reading RAR headers for {FileName} after {ElapsedSeconds}s",
                fileInfo.FileName, headerElapsed.TotalSeconds);
            throw new TimeoutException("Timed out while reading RAR headers (limit: 60s). This usually indicates a corrupt or malformed archive.");
        }
        catch (IOException ex)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error("[RarProcessor] IOException reading RAR headers for {FileName} after {ElapsedSeconds}s: {Message}",
                fileInfo.FileName, headerElapsed.TotalSeconds, ex.Message);
            throw;
        }
        catch (SharpCompress.Common.InvalidFormatException ex)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error("[RarProcessor] Invalid RAR format reading headers for {FileName} after {ElapsedSeconds}s: {Message}. Treating as missing/corrupt article.",
                fileInfo.FileName, headerElapsed.TotalSeconds, ex.Message);
            throw new UsenetArticleNotFoundException(fileInfo.NzbFile.Segments.FirstOrDefault()?.MessageId ?? "unknown");
        }
        catch (Exception ex)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error(ex, "[RarProcessor] Unexpected error reading RAR headers for {FileName} after {ElapsedSeconds}s: {Message}",
                fileInfo.FileName, headerElapsed.TotalSeconds, ex.Message);
            throw;
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
        
        var segmentSizes = fileInfo.NzbFile.Segments.Select(x => x.Size).ToArray();
        return usenet.GetFileStream(fileInfo.NzbFile.GetSegmentIds(), filesize, concurrency, usageContext, segmentSizes: segmentSizes);
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