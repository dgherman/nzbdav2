using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

public class RcloneRcService(ConfigManager configManager, IHttpClientFactory httpClientFactory)
{
    private const string RefreshEndpoint = "vfs/refresh";
    private const string ForgetEndpoint = "vfs/forget";

    public async Task<bool> RefreshAsync(string? dir = null)
    {
        var config = configManager.GetRcloneRcConfig();
        if (!config.Enabled || string.IsNullOrEmpty(config.Url)) return false;

        var parameters = new Dictionary<string, object>
        {
            ["recursive"] = "true"
        };

        if (!string.IsNullOrEmpty(dir))
        {
            parameters["dir"] = dir;
        }

        return await SendRequestAsync(config, RefreshEndpoint, parameters).ConfigureAwait(false);
    }

    public async Task<bool> ForgetAsync(string[] files)
    {
        var config = configManager.GetRcloneRcConfig();
        if (!config.Enabled || string.IsNullOrEmpty(config.Url)) return false;

        if (files.Length == 0) return true;

        // Batch all paths in a single request using dir, dir2, dir3, ... keys
        var parameters = new Dictionary<string, object>();
        for (var i = 0; i < files.Length; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            parameters[key] = files[i];
        }

        Log.Debug("[RcloneRc] Forgetting {Count} paths", files.Length);
        var success = await SendRequestAsync(config, ForgetEndpoint, parameters).ConfigureAwait(false);

        // Also delete from disk cache if configured
        foreach (var file in files)
        {
            DeleteFromDiskCache(config.CachePath, file);
        }

        return success;
    }

    /// <summary>
    /// Deletes a file from the rclone VFS disk cache.
    /// Rclone VFS cache mirrors the WebDAV path structure directly:
    /// e.g., /content/movies/Movie/Movie.mkv becomes:
    /// {CachePath}/vfs/{remote}/content/movies/Movie/Movie.mkv
    /// </summary>
    private void DeleteFromDiskCache(string? cachePath, string file)
    {
        if (string.IsNullOrEmpty(cachePath))
        {
            Log.Debug("[RcloneRc] CachePath not configured, skipping disk cache deletion");
            return;
        }

        try
        {
            // Normalize the file path - remove leading slashes for Path.Combine
            var relativePath = file.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relativePath))
            {
                Log.Debug("[RcloneRc] Empty file path, skipping cache deletion");
                return;
            }

            var cacheDir = cachePath.TrimEnd(Path.DirectorySeparatorChar);

            // Look for vfs subdirectory
            var vfsPath = Path.Combine(cacheDir, "vfs");
            if (!Directory.Exists(vfsPath))
            {
                Log.Debug("[RcloneRc] VFS cache directory not found: {Path}", vfsPath);
                return;
            }

            // Search all remote directories under vfs
            var remoteDirectories = Directory.GetDirectories(vfsPath);
            Log.Debug("[RcloneRc] Deleting from disk cache: {RelativePath} (searching {Count} remotes)",
                relativePath, remoteDirectories.Length);

            foreach (var remoteDir in remoteDirectories)
            {
                // Rclone VFS cache mirrors the path directly (no nested structure)
                var fullCachePath = Path.Combine(remoteDir, relativePath);

                if (File.Exists(fullCachePath))
                {
                    Log.Debug("[RcloneRc] Deleting cached file: {Path}", fullCachePath);
                    File.Delete(fullCachePath);
                }

                // Also check vfsMeta for metadata files
                var vfsMetaPath = Path.Combine(cacheDir, "vfsMeta", Path.GetFileName(remoteDir), relativePath);
                if (File.Exists(vfsMetaPath))
                {
                    Log.Debug("[RcloneRc] Deleting cached metadata: {Path}", vfsMetaPath);
                    File.Delete(vfsMetaPath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RcloneRc] Failed to delete cache file for: {File}", file);
        }
    }

    public async Task<(bool Success, string? Version, string? Error)> TestConnectionAsync(string host, string? user, string? pass)
    {
        var config = new RcloneRcConfig
        {
            Url = host,
            Username = user,
            Password = pass,
            Enabled = true
        };

        try
        {
            var client = httpClientFactory.CreateClient("RcloneRc");
            var url = config.Url!.TrimEnd('/') + "/core/version";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(config.Username) || !string.IsNullOrEmpty(config.Password))
            {
                var credentials = $"{config.Username ?? ""}:{config.Password ?? ""}";
                var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
            }
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(body);
                var version = doc.TryGetProperty("version", out var v) ? v.GetString() : null;
                return (true, version, null);
            }

            return (false, null, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private async Task<bool> SendRequestAsync(RcloneRcConfig config, string command, Dictionary<string, object> parameters)
    {
        try
        {
            var client = httpClientFactory.CreateClient("RcloneRc");
            var url = config.Url!.TrimEnd('/') + "/" + command;

            var request = new HttpRequestMessage(HttpMethod.Post, url);

            // Set Authentication if provided (user-only auth is valid for rclone)
            if (!string.IsNullOrEmpty(config.Username) || !string.IsNullOrEmpty(config.Password))
            {
                var credentials = $"{config.Username ?? ""}:{config.Password ?? ""}";
                var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
            }

            var json = JsonSerializer.Serialize(parameters);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            Log.Debug("[RcloneRc] {Command}: {Json}", command, json);

            var response = await client.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Log.Debug("[RcloneRc] {Command} succeeded", command);
                return true;
            }

            Log.Warning("[RcloneRc] {Command} failed with status {Status}: {Response}", command, response.StatusCode, responseBody);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            Log.Warning(ex, "[RcloneRc] {Command} timed out", command);
            return false;
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "[RcloneRc] {Command} failed: {Message}", command, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RcloneRc] {Command} unexpected error", command);
            return false;
        }
    }
}
