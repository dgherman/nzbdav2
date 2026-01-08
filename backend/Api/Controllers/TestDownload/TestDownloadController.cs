using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Api.Controllers.TestDownload;

[ApiController]
[Route("api/test-download/{davItemId}")]
public class TestDownloadController(
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager) : BaseApiController
{
    private const int TestSize = 10 * 1024 * 1024; // 10 MB

    protected override async Task<IActionResult> HandleRequest()
    {
        var davItemId = RouteData.Values["davItemId"]?.ToString();
        if (string.IsNullOrEmpty(davItemId) || !Guid.TryParse(davItemId, out var itemGuid))
        {
            return BadRequest(new { error = "Invalid DavItemId" });
        }

        var davItem = await dbClient.Ctx.Items
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == itemGuid)
            .ConfigureAwait(false);

        if (davItem == null)
        {
            return NotFound(new { error = "File not found" });
        }

        Log.Information("[BytePerfectTest] Starting test for file: {FileName} ({DavItemId}, Type: {Type})", davItem.Name, davItem.Id, davItem.Type);

        if (davItem.Type != DavItem.ItemType.NzbFile && 
            davItem.Type != DavItem.ItemType.RarFile && 
            davItem.Type != DavItem.ItemType.MultipartFile)
        {
            Log.Warning("[BytePerfectTest] Aborted: File type {Type} is not supported for testing.", davItem.Type);
            return BadRequest(new { error = $"File type {davItem.Type} is not supported for byte-perfect testing." });
        }

        try
        {
            // Perform test twice
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (hash1, bytes1) = await DownloadAndHashAsync(davItem, 1);
            var time1 = sw.Elapsed;
            var hash1Str = Convert.ToHexString(hash1);
            Log.Information("[BytePerfectTest] Attempt 1 complete. Downloaded: {Bytes:N0} bytes, Time: {Time:F2}s, MD5: {Hash}", bytes1, time1.TotalSeconds, hash1Str);

            sw.Restart();
            var (hash2, bytes2) = await DownloadAndHashAsync(davItem, 2);
            var time2 = sw.Elapsed;
            var hash2Str = Convert.ToHexString(hash2);
            Log.Information("[BytePerfectTest] Attempt 2 complete. Downloaded: {Bytes:N0} bytes, Time: {Time:F2}s, MD5: {Hash}", bytes2, time2.TotalSeconds, hash2Str);

            var matches = hash1.SequenceEqual(hash2);
            
            if (matches)
            {
                Log.Information("[BytePerfectTest] SUCCESS: Both downloads match exactly for {FileName}. MD5: {Hash}. Total Downloaded: {Bytes:N0} bytes.", davItem.Name, hash1Str, bytes1);
            }
            else
            {
                Log.Error("[BytePerfectTest] FAILURE: Hash mismatch for {FileName}! Data is inconsistent. Hash1: {H1}, Hash2: {H2}", davItem.Name, hash1Str, hash2Str);
            }

            return Ok(new
            {
                matches,
                hash1 = hash1Str,
                hash2 = hash2Str,
                bytesRead = bytes1,
                message = matches 
                    ? $"Success: Both {bytes1:N0} byte downloads are identical. MD5: {hash1Str}" 
                    : $"Failure: The two downloads returned different data. H1: {hash1Str}, H2: {hash2Str}"
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BytePerfectTest] Error during test execution");
            return StatusCode(500, new { error = $"Test failed: {ex.Message}" });
        }
    }

    private async Task<(byte[] Hash, long BytesRead)> DownloadAndHashAsync(DavItem davItem, int attempt)
    {
        var usageContext = new ConnectionUsageContext(
            ConnectionUsageType.Streaming,
            new ConnectionUsageDetails 
            { 
                Text = $"{davItem.Path} (Byte-Perfect Test {attempt})",
                DavItemId = davItem.Id,
                FileDate = davItem.ReleaseDate
            }
        );

        await using var stream = await GetStreamForType(davItem, usageContext);

        var buffer = new byte[65536];
        long totalRead = 0;
        using var md5 = MD5.Create();
        
        while (totalRead < TestSize)
        {
            var toRead = (int)Math.Min(buffer.Length, TestSize - totalRead);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), HttpContext.RequestAborted);
            if (read == 0) break;
            
            md5.TransformBlock(buffer, 0, read, null, 0);
            totalRead += read;
        }

        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (md5.Hash!, totalRead);
    }

    private async Task<Stream> GetStreamForType(DavItem davItem, ConnectionUsageContext usageContext)
    {
        var id = davItem.Id;
        switch (davItem.Type)
        {
            case DavItem.ItemType.NzbFile:
                var nzbFile = await dbClient.GetNzbFileAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
                if (nzbFile is null) throw new FileNotFoundException($"NZB file metadata not found for {id}");
                
                var segmentSizes = nzbFile.GetSegmentSizes();
                if (segmentSizes == null)
                {
                    Log.Information("[BytePerfectTest] Segment sizes missing. Running immediate analysis...");
                    segmentSizes = await usenetClient.AnalyzeNzbAsync(nzbFile.SegmentIds, 10, null, HttpContext.RequestAborted);
                }

                return usenetClient.GetFileStream(
                    nzbFile.SegmentIds,
                    davItem.FileSize ?? 0,
                    configManager.GetConnectionsPerStream(),
                    usageContext,
                    true,
                    configManager.GetStreamBufferSize(),
                    segmentSizes
                );

            case DavItem.ItemType.RarFile:
                var rarFile = await dbClient.Ctx.RarFiles.Where(x => x.Id == id).FirstOrDefaultAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                if (rarFile is null) throw new FileNotFoundException($"RAR file metadata not found for {id}");
                return new DavMultipartFileStream
                (
                    rarFile.ToDavMultipartFileMeta().FileParts,
                    usenetClient,
                    configManager.GetConnectionsPerStream(),
                    usageContext
                );

            case DavItem.ItemType.MultipartFile:
                var multipartFile = await dbClient.Ctx.MultipartFiles.Where(x => x.Id == id).FirstOrDefaultAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                if (multipartFile is null) throw new FileNotFoundException($"Multipart file metadata not found for {id}");
                var packedStream = new DavMultipartFileStream(
                    multipartFile.Metadata.FileParts,
                    usenetClient,
                    configManager.GetConnectionsPerStream(),
                    usageContext
                );
                return multipartFile.Metadata.AesParams != null
                    ? new AesDecoderStream(packedStream, multipartFile.Metadata.AesParams)
                    : packedStream;

            default:
                throw new NotSupportedException($"Unsupported item type: {davItem.Type}");
        }
    }
}