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
    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        Log.Information("[RarProcessor] Starting RAR processing for {Count} parts", fileInfos.Count);

        var allSegments = new List<StoredFileSegment>();

        foreach (var fileInfo in fileInfos.OrderBy(f => GetPartNumber(f.FileName)))
        {
            if (fileInfo.MissingFirstSegment)
            {
                Log.Warning("[RarProcessor] Skipping part {FileName} because the first segment is missing.", fileInfo.FileName);
                continue;
            }

            Log.Debug("[RarProcessor] Reading headers for part {FileName}", fileInfo.FileName);
            
            try 
            {
                var segments = await ProcessPartAsync(fileInfo);
                allSegments.AddRange(segments);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[RarProcessor] Failed to process part {FileName}: {Message}", fileInfo.FileName, ex.Message);
                // Continue to next part, maybe we can still get some files
            }
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
        await using var stream = await GetNzbFileStream(fileInfo).ConfigureAwait(false);
        
        if (fileInfo.MagicOffset > 0)
        {
            stream.Seek(fileInfo.MagicOffset, SeekOrigin.Begin);
        }

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(60));

        var headers = await RarUtil.GetRarHeadersAsync(stream, password, headerCts.Token).ConfigureAwait(false);
        
        var archiveName = GetArchiveName(fileInfo);
        var partNumber = GetPartNumber(fileInfo.FileName);
        var offset = Math.Max(0, fileInfo.MagicOffset);

        return headers
            .Where(x => x.HeaderType == HeaderType.File)
            .Select(x => new StoredFileSegment()
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
                ReleaseDate = fileInfo.ReleaseDate,
            }).ToList();
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

    private async Task<NzbFileStream> GetNzbFileStream(GetFileInfosStep.FileInfo fileInfo)
    {
        var filesize = fileInfo.FileSize ?? await usenet.GetFileSizeAsync(fileInfo.NzbFile, ct).ConfigureAwait(false);
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