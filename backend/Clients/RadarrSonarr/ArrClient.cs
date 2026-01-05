using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class ArrClient(string host, string apiKey)
{
    // Define ArrEventType enum
    public enum ArrEventType
    {
        Unknown = 0,
        Grabbed = 1,
        SeriesFolderImported = 2,
        DownloadFolderImported = 3,
        DownloadFailed = 4,
        EpisodeFileDeleted = 5,
        EpisodeFileRenamed = 6,
        // ... add other relevant event types if needed
    }

    protected static readonly HttpClient HttpClient = new HttpClient();

    public string Host { get; } = host;
    private string ApiKey { get; } = apiKey;
    private const string BasePath = "/api/v3";

    public Task<ArrApiInfoResponse> GetApiInfo() =>
        GetRoot<ArrApiInfoResponse>($"/api");

    public virtual Task<bool> RemoveAndSearch(string symlinkOrStrmPath, int? episodeId = null, string sortKey = "date", string sortDirection = "descending") =>
        throw new InvalidOperationException();

    public Task<List<ArrRootFolder>> GetRootFolders() =>
        Get<List<ArrRootFolder>>($"/rootfolder");

    public Task<List<ArrDownloadClient>> GetDownloadClientsAsync() =>
        Get<List<ArrDownloadClient>>($"/downloadClient");

    public Task<ArrCommand> RefreshMonitoredDownloads() =>
        CommandAsync(new { name = "RefreshMonitoredDownloads" });

    public Task<ArrQueueStatus> GetQueueStatusAsync() =>
        Get<ArrQueueStatus>($"/queue/status");

    public Task<ArrQueue<ArrQueueRecord>> GetQueueAsync() =>
        Get<ArrQueue<ArrQueueRecord>>($"/queue?protocol=usenet&pageSize=5000");

    public async Task<int> GetQueueCountAsync() =>
        (await Get<ArrQueue<ArrQueueRecord>>($"/queue?pageSize=1")).TotalRecords;

    public Task<HttpStatusCode> DeleteQueueRecord(int id, DeleteQueueRecordRequest request) =>
        Delete($"/queue/{id}", request.GetQueryParams());

    public Task<HttpStatusCode> DeleteQueueRecord(int id, ArrConfig.QueueAction request) =>
        request is not ArrConfig.QueueAction.DoNothing
            ? Delete($"/queue/{id}", new DeleteQueueRecordRequest(request).GetQueryParams())
            : Task.FromResult(HttpStatusCode.OK);

    public Task<ArrCommand> CommandAsync(object command) =>
        Post<ArrCommand>($"/command", command);

    public virtual Task<ArrHistory> GetHistoryAsync(int? movieId = null, int? seriesId = null, int? episodeId = null, int pageSize = 1000, string sortKey = "date", string sortDirection = "descending")
    {
        var query = $"?pageSize={pageSize}&sortKey={sortKey}&sortDirection={sortDirection}";
        if (movieId.HasValue) query += $"&movieId={movieId.Value}&eventType={(int)ArrEventType.Grabbed}";
        if (seriesId.HasValue) query += $"&seriesIds={seriesId.Value}&eventType={(int)ArrEventType.Grabbed}";
        // Note: Base implementation doesn't handle episodeId generic logic well because Sonarr/Radarr differ significantly here.
        // Derived classes should override this.
        return Get<ArrHistory>($"/history{query}");
    }

    public Task<ArrHistory> GetRecentImportsAsync(int pageSize = 100)
    {
        // Fetch recent "DownloadFolderImported" events (Type 3)
        return Get<ArrHistory>($"/history?page=1&pageSize={pageSize}&sortKey=date&sortDirection=descending&eventType={(int)ArrEventType.DownloadFolderImported}");
    }

    public async Task<bool> MarkHistoryFailedAsync(int historyId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GetRequestUri($"/history/failed/{historyId}"));
        using var response = await SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error($"[ArrClient] MarkHistoryFailed failed with status {response.StatusCode}");
            return false;
        }

        return true;
    }

    protected Task<T> Get<T>(string path) =>
        GetRoot<T>($"{BasePath}{path}");

    protected async Task<T> GetRoot<T>(string rootPath)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Host}{rootPath}");
        using var response = await SendAsync(request);
        
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Request to {rootPath} failed with status {response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength == 0)
            return default!;

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new NullReferenceException();
    }

    protected async Task<T> Post<T>(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GetRequestUri(path));
        var jsonBody = JsonSerializer.Serialize(body);
        request.Content = new StringContent(jsonBody, new MediaTypeHeaderValue("application/json"));
        using var response = await SendAsync(request);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Request to {path} failed with status {response.StatusCode}");

        if (response.Content.Headers.ContentLength == 0)
            return default!;

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new NullReferenceException();
    }

    protected async Task<HttpStatusCode> Delete(string path, Dictionary<string, string>? queryParams = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, GetRequestUri(path, queryParams));
        using var response = await SendAsync(request);
        return response.StatusCode;
    }

    private string GetRequestUri(string path, Dictionary<string, string>? queryParams = null)
    {
        queryParams ??= new Dictionary<string, string>();
        var resource = $"{Host}{BasePath}{path}";
        var query = queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        var queryString = string.Join("&", query);
        if (queryString.Length > 0) resource = $"{resource}?{queryString}";
        return resource;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        request.Headers.Add("X-Api-Key", ApiKey);
        return HttpClient.SendAsync(request);
    }
}