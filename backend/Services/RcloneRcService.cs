using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        var allSuccess = true;
        foreach (var file in files)
        {
            Log.Information("[RcloneRc] Flushing cache for file: {FilePath}", file);
            var parameters = new Dictionary<string, object>
            {
                ["file"] = file
            };

            if (!await SendRequestAsync(config, ForgetEndpoint, parameters).ConfigureAwait(false))
            {
                allSuccess = false;
            }

            // Also delete from disk cache if configured
            DeleteFromDiskCache(config.CachePath, file);
        }

        return allSuccess;
    }

    /// <summary>
    /// Deletes a file from the rclone VFS disk cache.
    /// Rclone VFS cache uses a nested directory structure based on the first 5 characters of the filename.
    /// e.g., .ids/4e5b250e-30ae-484c-ab9e-573beb7eb6a6 becomes:
    /// {CachePath}/vfs/{remote}/.ids/4/e/5/b/2/4e5b250e-30ae-484c-ab9e-573beb7eb6a6
    /// </summary>
    private void DeleteFromDiskCache(string? cachePath, string file)
    {
        if (string.IsNullOrEmpty(cachePath)) return;

        try
        {
            // Extract the guid from the file path (e.g., ".ids/4e5b250e-30ae-484c-ab9e-573beb7eb6a6")
            var fileName = Path.GetFileName(file);
            if (string.IsNullOrEmpty(fileName) || fileName.Length < 5) return;

            // Build the nested cache path structure (first 5 chars split into directories)
            var nestedPath = string.Join(Path.DirectorySeparatorChar.ToString(),
                fileName[0].ToString(),
                fileName[1].ToString(),
                fileName[2].ToString(),
                fileName[3].ToString(),
                fileName[4].ToString(),
                fileName);

            // Get the parent directory from the original file path
            var parentDir = Path.GetDirectoryName(file)?.Replace('/', Path.DirectorySeparatorChar) ?? "";

            // Search for matching files in the cache directory
            var cacheDir = cachePath.TrimEnd(Path.DirectorySeparatorChar);

            // Look for vfs subdirectory
            var vfsPath = Path.Combine(cacheDir, "vfs");
            if (!Directory.Exists(vfsPath))
            {
                Log.Debug("[RcloneRc] VFS cache directory not found: {Path}", vfsPath);
                return;
            }

            // Search all remote directories under vfs
            foreach (var remoteDir in Directory.GetDirectories(vfsPath))
            {
                var fullCachePath = Path.Combine(remoteDir, parentDir, nestedPath);
                if (File.Exists(fullCachePath))
                {
                    Log.Information("[RcloneRc] Deleting cached file: {Path}", fullCachePath);
                    File.Delete(fullCachePath);
                }

                // Also check vfsMeta for metadata files
                var vfsMetaPath = Path.Combine(cacheDir, "vfsMeta", Path.GetFileName(remoteDir), parentDir, nestedPath);
                if (File.Exists(vfsMetaPath))
                {
                    Log.Information("[RcloneRc] Deleting cached metadata: {Path}", vfsMetaPath);
                    File.Delete(vfsMetaPath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RcloneRc] Failed to delete cache file for: {File}", file);
        }
    }

    private async Task<bool> SendRequestAsync(RcloneRcConfig config, string command, Dictionary<string, object> parameters)
    {
        try
        {
            var client = httpClientFactory.CreateClient("RcloneRc");
            var url = config.Url!.TrimEnd('/') + "/" + command;

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            
            // Set Authentication if provided
            if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
            {
                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config.Username}:{config.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
            }

            var json = JsonSerializer.Serialize(parameters);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            Log.Information("[RcloneRc] Sending command {Command} to {Url} with parameters: {Json}", command, url, json);

            var response = await client.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("[RcloneRc] Command {Command} successful. Response: {Response}", command, responseBody);
                return true;
            }
            else
            {
                Log.Warning("[RcloneRc] Command {Command} failed with status {Status}. Response: {Response}", command, response.StatusCode, responseBody);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RcloneRc] Failed to send command {Command}", command);
            return false;
        }
    }
}
