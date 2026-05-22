using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.AddUrl;

public class AddUrlRequest() : AddFileRequest
{
    private static readonly HttpClient HttpClient = GetHttpClient();

    private const int MaxAutomaticRedirections = 10;

    public static async Task<AddUrlRequest> New(HttpContext context, ConfigManager configManager)
    {
        var nzbUrl = context.GetQueryParam("name");
        var nzbName = context.GetQueryParam("nzbname");
        var userAgent = configManager.GetUserAgent();
        var nzbFile = await GetNzbFile(nzbUrl, nzbName, userAgent).ConfigureAwait(false);
        return new AddUrlRequest()
        {
            FileName = nzbFile.FileName,
            ContentType = nzbFile.ContentType,
            NzbFileContents = nzbFile.FileContents,
            Category = context.GetQueryParam("cat") ?? configManager.GetManualUploadCategory(),
            Priority = MapPriorityOption(context.GetQueryParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetQueryParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    private static async Task<NzbFileResponse> GetNzbFile(string? url, string? nzbName, string userAgent)
    {
        try
        {
            // validate url
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception($"The url is invalid.");

            // fetch url
            var response = await GetAsync(url, userAgent).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Received status code {response.StatusCode}.");

            // read the content type
            var contentType = response.Content.Headers.ContentType?.MediaType;

            // determine the filename
            var fileName = AddNzbExtension(nzbName)
                           ?? GetFilenameFromResponseHeader(response)
                           ?? GetFilenameFromUrl(url)
                           ?? throw new Exception("Filename could not be determined from Content-Disposition header or URL.");

            // read the file contents
            var fileContents = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(fileContents))
                throw new Exception("NZB file contents are empty.");

            // return response
            return new NzbFileResponse
            {
                FileName = fileName,
                ContentType = contentType,
                FileContents = fileContents
            };
        }
        catch (Exception ex)
        {
            throw new BadHttpRequestException($"Failed to fetch nzb-file url `{url}`: {ex.Message}");
        }
    }

    private static string? GetFilenameFromResponseHeader(HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var filename = contentDisposition?.FileName?.Trim('"');
        return StringUtil.EmptyToNull(filename);
    }

    private static string? GetFilenameFromUrl(string url)
    {
        try
        {
            var filename = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(filename)) return null;
            filename = Uri.UnescapeDataString(filename);
            return AddNzbExtension(filename);
        }
        catch
        {
            return null;
        }
    }

    private static string? AddNzbExtension(string? nzbName)
    {
        return nzbName == null ? null
            : nzbName.ToLower().EndsWith("nzb") ? nzbName
            : $"{nzbName}.nzb";
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, string userAgent)
    {
        var response = await SendAsync(url, userAgent);
        var remainingRedirects = MaxAutomaticRedirections;
        while
        (
            (int)response.StatusCode is >= 300 and < 400
            && remainingRedirects > 0
            && response.Headers.Location is not null
            && EnvironmentUtil.IsVariableTrue("ALLOW_HTTPS_TO_HTTP_REDIRECTS")
        )
        {
            var redirect = response.Headers.Location;
            var redirectUri = redirect.IsAbsoluteUri ? redirect : new Uri(new Uri(url), redirect);
            response = await SendAsync(redirectUri.ToString(), userAgent);
            remainingRedirects--;
        }

        return response;
    }

    // Set the User-Agent per-request rather than on the shared HttpClient's default
    // headers, so concurrent addurl requests with different configured user-agents
    // do not race on a single mutable header collection.
    private static Task<HttpResponseMessage> SendAsync(string url, string userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return HttpClient.SendAsync(request);
    }

    private static HttpClient GetHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxAutomaticRedirections,
        };
        return new HttpClient(handler);
    }

    private class NzbFileResponse
    {
        public required string FileName { get; init; }
        public required string? ContentType { get; init; }
        public required string FileContents { get; init; }
    }
}