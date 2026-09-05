using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NWebDav.Server.Stores;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

[ApiController]
[Route("view/{*path}")]
public class ListWebdavDirectoryController(DatabaseStore store, ConfigManager configManager) : ControllerBase
{

    private async Task<Stream> GetWebdavItem(GetWebdavItemRequest request)
    {
        var item = await store.GetItemAsync(request.Item, HttpContext.RequestAborted).ConfigureAwait(false);
        if (item is null) throw new BadHttpRequestException("The file does not exist.");
        if (item is IStoreCollection) throw new BadHttpRequestException("The file does not exist.");

        // handle par2 preview
        if (Path.GetExtension(item.Name).ToLower() == ".par2" && configManager.IsPreviewPar2FilesEnabled())
            return await GetPar2PreviewStream(item).ConfigureAwait(false);

        // get the file stream and set the file-size in header
        var stream = await item.GetReadableStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var fileSize = stream.Length;

        // set content headers
        Response.Headers["Content-Type"] = GetContentType(item.Name);
        Response.Headers["Accept-Ranges"] = "bytes";
        Response.Headers["Content-Disposition"] = GetContentDisposition(item.Name, request.ShouldDownload);

        var resolvedRange = ResolveByteRange(request, fileSize);
        if (resolvedRange is not null)
        {
            var (start, end, chunkSize, isBoundedEnd) = resolvedRange.Value;

            // seek
            stream.Seek(start, SeekOrigin.Begin);
            if (isBoundedEnd) stream = stream.LimitLength(chunkSize);

            // set response headers
            Response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileSize}";
            Response.Headers["Content-Length"] = chunkSize.ToString();
            Response.StatusCode = 206;
        }
        else
        {
            Response.Headers["Content-Length"] = fileSize.ToString();
        }

        return stream;
    }

    [HttpGet]
    public async Task HandleRequest()
    {
        try
        {
            HttpContext.Items["configManager"] = configManager;
            var request = new GetWebdavItemRequest(HttpContext);
            await using var response = await GetWebdavItem(request).ConfigureAwait(false);
            await response.CopyToAsync(Response.Body, bufferSize: 256 * 1024, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = 401;
        }
        catch (BadHttpRequestException ex)
        {
            Response.StatusCode = ex.StatusCode;
            await Response.WriteAsync(ex.Message, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Response.StatusCode = 500;
            await Response.WriteAsync($"Error streaming file: {ex.Message}", HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves a request's byte range against the now-known file size. Handles normal ranges
    /// (bytes=N-M, bytes=N-) as well as the suffix form (bytes=-N, RFC 9110 14.1.2: "the last N
    /// bytes"), clamping a suffix >= fileSize to the whole file instead of going negative.
    /// Returns null when the request has no range at all.
    /// </summary>
    internal static (long Start, long End, long ChunkSize, bool HasExplicitEnd)? ResolveByteRange(
        GetWebdavItemRequest request, long fileSize)
    {
        long? start = request.RangeStart;
        long? end = request.RangeEnd;
        if (request.SuffixLength is not null)
        {
            var suffixLength = Math.Clamp(request.SuffixLength.Value, 0, fileSize);
            start = fileSize - suffixLength;
            end = null;
        }

        if (start is null) return null;

        var resolvedEnd = end ?? (fileSize - 1);
        var chunkSize = 1 + resolvedEnd - start.Value;
        return (start.Value, resolvedEnd, chunkSize, end is not null);
    }

    private static string GetContentDisposition(string filename, bool shouldDownload)
    {
        var disposition = shouldDownload ? "attachment" : "inline";

        // Strip control characters for header safety
        var safe = Regex.Replace(filename, @"[\x00-\x1f]", "");

        // ASCII fallback: replace non-ASCII, quotes, backslashes, semicolons
        var ascii = Regex.Replace(safe, @"[^\x20-\x7E]|["";\\]", "_");

        // RFC 5987 UTF-8 encoding for the full filename
        var utf8 = Uri.EscapeDataString(safe);

        return $"{disposition}; filename=\"{ascii}\"; filename*=UTF-8''{utf8}";
    }

    private static string GetContentType(string item)
    {
        var extension = Path.GetExtension(item).ToLower();
        return extension == ".mkv" ? "video/webm"
            : extension == ".rclonelink" ? "text/plain"
            : extension == ".nfo" ? "text/plain"
            : ContentTypeUtil.GetContentType(Path.GetFileName(item));
    }

    private async Task<Stream> GetPar2PreviewStream(IStoreItem item)
    {
        Response.Headers.ContentType = "text/plain";
        await using var stream = await item.GetReadableStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var fileDescriptors = await Par2.ReadFileDescriptions(stream, HttpContext.RequestAborted).GetAllAsync()
            .ConfigureAwait(false);
        return new MemoryStream(Encoding.UTF8.GetBytes(fileDescriptors.ToIndentedJson()));
    }
}