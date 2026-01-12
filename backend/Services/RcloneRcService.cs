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

    public async Task RefreshAsync(string? dir = null)
    {
        var config = configManager.GetRcloneRcConfig();
        if (!config.Enabled || string.IsNullOrEmpty(config.Url)) return;

        var parameters = new Dictionary<string, object>
        {
            ["recursive"] = "true"
        };
        
        if (!string.IsNullOrEmpty(dir))
        {
            parameters["dir"] = dir;
        }

        await SendRequestAsync(config, RefreshEndpoint, parameters).ConfigureAwait(false);
    }

    public async Task ForgetAsync(string[] files)
    {
        var config = configManager.GetRcloneRcConfig();
        if (!config.Enabled || string.IsNullOrEmpty(config.Url)) return;

        if (files.Length == 0) return;

        var parameters = new Dictionary<string, object>
        {
            ["files"] = files
        };

        await SendRequestAsync(config, ForgetEndpoint, parameters).ConfigureAwait(false);
    }

    private async Task SendRequestAsync(RcloneRcConfig config, string command, Dictionary<string, object> parameters)
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

            Log.Debug("[RcloneRc] Sending command {Command} to {Url} with parameters: {Json}", command, url, json);

            var response = await client.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Log.Debug("[RcloneRc] Command {Command} successful. Response: {Response}", command, responseBody);
            }
            else
            {
                Log.Warning("[RcloneRc] Command {Command} failed with status {Status}. Response: {Response}", command, response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RcloneRc] Failed to send command {Command}", command);
        }
    }
}
