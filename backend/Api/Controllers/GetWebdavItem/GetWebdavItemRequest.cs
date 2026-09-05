using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

public class GetWebdavItemRequest
{
    public string Item { get; init; }
    public long? RangeStart { get; init; }
    public long? RangeEnd { get; init; }
    public long? SuffixLength { get; init; }
    public bool ShouldDownload { get; init; }

    public GetWebdavItemRequest(HttpContext context)
    {
        // normalize path
        var path = context.Request.Path.Value;
        if (path.StartsWith("/")) path = path[1..];
        if (path.StartsWith("view")) path = path[4..];
        if (path.StartsWith("/")) path = path[1..];
        Item = path;

        // parse download query param
        ShouldDownload = context.Request.Query["download"].FirstOrDefault()?.ToLower() == "true";

        // authenticate the downloadKey
        var downloadKey = context.Request.Query["downloadKey"];
        var configManager = (ConfigManager)context.Items["configManager"]!;
        if (!VerifyDownloadKey(downloadKey, Item, configManager))
            throw new UnauthorizedAccessException("Invalid download key");

        // parse range header (RFC 9110 14.1.2: "bytes=first-last", "bytes=first-", or the
        // suffix form "bytes=-suffixLength" meaning "the last suffixLength bytes")
        var rangeHeader = context.Request.Headers["Range"].FirstOrDefault() ?? "";
        if (!rangeHeader.StartsWith("bytes=")) return;
        var spec = rangeHeader[6..];
        var dashIndex = spec.IndexOf('-');
        if (dashIndex < 0) return;
        var startPart = spec[..dashIndex];
        var endPart = spec[(dashIndex + 1)..];

        if (startPart.Length == 0)
        {
            // suffix range: bytes=-N. Resolved against the actual file size in the controller,
            // once it is known, since that's the only place the total length is available.
            if (endPart.Length == 0) return;
            SuffixLength = long.Parse(endPart);
        }
        else
        {
            RangeStart = long.Parse(startPart);
            if (endPart.Length > 0) RangeEnd = long.Parse(endPart);
        }
    }

    private static bool VerifyDownloadKey(string? downloadKey, string path, ConfigManager configManager)
    {
        if (string.IsNullOrEmpty(downloadKey))
            return false;

        // Check static download key first (works for all paths)
        var staticKey = configManager.GetStaticDownloadKey();
        if (downloadKey == staticKey)
            return true;

        if (path.StartsWith(".ids"))
        {
            // strm streams link items by id and use a different download key
            var strmKey = configManager.GetStrmKey();
            var expectedDownloadKey = GenerateDownloadKey(strmKey, path);
            if (downloadKey == expectedDownloadKey)
                return true;
        }

        // Per-path download key using frontend API key
        var apiKey = EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
        return downloadKey == GenerateDownloadKey(apiKey, path);
    }

    public static string GenerateDownloadKey(string apiKey, string path)
    {
        var input = $"{path}_{apiKey}";
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(inputBytes);
        var hash = Convert.ToHexStringLower(hashBytes);
        return hash;
    }
}