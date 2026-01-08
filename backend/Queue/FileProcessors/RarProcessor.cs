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
        Log.Information("[RarProcessor] Starting RAR processing for {FileName} ({Count} parts)", _primaryFile.FileName, fileInfos.Count);

        if (_primaryFile.MissingFirstSegment)
        {
            Log.Error("[RarProcessor] Skipping {FileName} because the first segment is missing.", _primaryFile.FileName);
            throw new NzbWebDAV.Exceptions.UsenetArticleNotFoundException(_primaryFile.NzbFile.Segments.FirstOrDefault()?.MessageId ?? "unknown");
        }

        Log.Debug("[RarProcessor] Initializing joined stream for {FileName}. Total parts: {Count}",
            _primaryFile.FileName, fileInfos.Count);
        await using var stream = await GetJoinedStream().ConfigureAwait(false);
        Log.Debug("[RarProcessor] Stream initialized. Length: {StreamLength}", stream.Length);

        if (_primaryFile.MagicOffset > 0)
        {
            Log.Information("[RarProcessor] Seeking to RAR magic at offset {Offset} for {FileName}", _primaryFile.MagicOffset, _primaryFile.FileName);
            stream.Seek(_primaryFile.MagicOffset, SeekOrigin.Begin);
        }

        // Create a linked token source with a timeout for the header parsing operation
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(120)); // Increased timeout for joined streams

        Log.Information("[RarProcessor] Reading RAR headers for {FileName} (timeout 120s)...", _primaryFile.FileName);
        var headerStartTime = DateTime.UtcNow;
        List<IRarHeader> headers;
        try
        {
            headers = await RarUtil.GetRarHeadersAsync(stream, password, headerCts.Token).ConfigureAwait(false);
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Information("[RarProcessor] Successfully read {HeaderCount} RAR headers for {FileName} in {ElapsedSeconds}s",
                headers.Count, _primaryFile.FileName, headerElapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (headerCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error("[RarProcessor] Timeout reading RAR headers for {FileName} after {ElapsedSeconds}s",
                _primaryFile.FileName, headerElapsed.TotalSeconds);
            throw new TimeoutException("Timed out while reading RAR headers (limit: 120s). This usually indicates a corrupt or malformed archive.");
        }
        catch (IOException ex)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error("[RarProcessor] IOException reading RAR headers for {FileName} after {ElapsedSeconds}s: {Message}",
                _primaryFile.FileName, headerElapsed.TotalSeconds, ex.Message);
            throw;
        }
        catch (SharpCompress.Common.InvalidFormatException ex)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error("[RarProcessor] Invalid RAR format reading headers for {FileName} after {ElapsedSeconds}s: {Message}. Treating as missing/corrupt article.",
                _primaryFile.FileName, headerElapsed.TotalSeconds, ex.Message);
            throw new UsenetArticleNotFoundException(_primaryFile.NzbFile.Segments.FirstOrDefault()?.MessageId ?? "unknown");
        }
        catch (Exception ex)
        {
            var headerElapsed = DateTime.UtcNow - headerStartTime;
            Log.Error(ex, "[RarProcessor] Unexpected error reading RAR headers for {FileName} after {ElapsedSeconds}s: {Message}",
                _primaryFile.FileName, headerElapsed.TotalSeconds, ex.Message);
            throw;
        }

        var archiveName = GetArchiveName();
        var offset = Math.Max(0, _primaryFile.MagicOffset);

        // Map the joined stream results back to individual parts
        var sortedFileInfos = fileInfos.OrderBy(f => GetPartNumber(f.FileName)).ToList();
        var partOffsets = new List<long>();
        long currentOffset = 0;
        foreach (var fi in sortedFileInfos)
        {
            partOffsets.Add(currentOffset);
            currentOffset += fi.FileSize ?? 0;
        }

        return new Result()
        {
            StoredFileSegments = headers
                .Where(x => x.HeaderType == HeaderType.File)
                .Select(x =>
                {
                    var fileStartInJoinedStream = x.GetDataStartPosition() + offset;
                    var fileSize = x.GetAdditionalDataSize();
                    
                    // Split the file data across volumes
                    var segments = new List<StoredFileSegment>();
                    long bytesMapped = 0;
                    
                    while (bytesMapped < fileSize)
                    {
                        var absolutePos = fileStartInJoinedStream + bytesMapped;
                        // Find which volume contains this position
                        var partIndex = -1;
                        for (int i = partOffsets.Count - 1; i >= 0; i--)
                        {
                            if (absolutePos >= partOffsets[i]) { partIndex = i; break; }
                        }
                        
                        if (partIndex < 0) break; // Should not happen

                        var fi = sortedFileInfos[partIndex];
                        var posInPart = absolutePos - partOffsets[partIndex];
                        var bytesRemainingInPart = (fi.FileSize ?? 0) - posInPart;
                        var bytesToMap = Math.Min(fileSize - bytesMapped, bytesRemainingInPart);
                        
                        if (bytesToMap <= 0) break;

                        segments.Add(new StoredFileSegment()
                        {
                            NzbFile = fi.NzbFile,
                            PartSize = fi.FileSize ?? 0,
                            ArchiveName = archiveName,
                            PartNumber = GetPartNumber(fi.FileName),
                            PathWithinArchive = x.GetFileName(),
                            ByteRangeWithinPart = LongRange.FromStartAndSize(posInPart, bytesToMap),
                            AesParams = x.GetAesParams(password),
                            ReleaseDate = fi.ReleaseDate,
                        });
                        
                        bytesMapped += bytesToMap;
                    }

                    return segments;
                })
                .SelectMany(x => x)
                .ToArray(),
        };
    }

    private string GetArchiveName()
    {
        return FilenameUtil.GetMultipartBaseName(_primaryFile.FileName);
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

    private async Task<Stream> GetJoinedStream()
    {
        var sortedFileInfos = fileInfos.OrderBy(f => GetPartNumber(f.FileName)).ToList();
        var parts = new List<DavMultipartFile.FilePart>();
        
        foreach (var fi in sortedFileInfos)
        {
            var partSize = fi.FileSize ?? await usenet.GetFileSizeAsync(fi.NzbFile, ct).ConfigureAwait(false);
            fi.FileSize = partSize; // Store for offset calculation
            parts.Add(new DavMultipartFile.FilePart
            {
                SegmentIds = fi.NzbFile.GetSegmentIds(),
                SegmentIdByteRange = LongRange.FromStartAndSize(0, partSize),
                FilePartByteRange = LongRange.FromStartAndSize(0, partSize)
            });
        }

        var usageContext = ct.GetContext<ConnectionUsageContext>();
        return new DavMultipartFileStream(parts.ToArray(), usenet, 1, usageContext);
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
