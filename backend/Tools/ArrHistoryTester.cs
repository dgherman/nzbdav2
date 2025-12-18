using NzbWebDAV.Clients.RadarrSonarr;
using Serilog;

namespace NzbWebDAV.Tools;

public static class ArrHistoryTester
{
    public static async Task RunAsync(string[] args)
    {
        // Expected args: --test-arr-history <type> <host> <apiKey> <releaseName>
        // type: sonarr or radarr
        
        if (args.Length < 5)
        {
            Console.WriteLine("Usage: --test-arr-history <type> <host> <apiKey> <releaseName>");
            return;
        }

        var type = args[1].ToLower();
        var host = args[2];
        var apiKey = args[3];
        var releaseName = args[4];

        Log.Information($"Starting ArrHistoryTester for {type} at {host} with release '{releaseName}'");

        ArrClient client = type switch
        {
            "sonarr" => new SonarrClient(host, apiKey),
            "radarr" => new RadarrClient(host, apiKey),
            _ => throw new ArgumentException("Invalid type. Must be 'sonarr' or 'radarr'.")
        };

        try
        {
            // 1. Fetch History
            Log.Information("Fetching history...");
            var history = await client.GetHistoryAsync();
            
            Log.Information($"Fetched {history.Records.Count} history records.");

            // 2. Search for Release
            Log.Information($"Searching for release '{releaseName}'...");
            var grabEvent = history.Records.FirstOrDefault(x => 
                x.SourceTitle != null &&
                x.SourceTitle.Equals(releaseName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.EventType, "grabbed", StringComparison.OrdinalIgnoreCase)
            );

            if (grabEvent == null)
            {
                Log.Warning("Grab event NOT FOUND.");
                
                // Dump top 10 for debug
                Log.Information("Top 10 History Records:");
                foreach (var record in history.Records.Take(10))
                {
                    Log.Information($" - [{record.Id}] {record.SourceTitle} ({record.EventType})");
                }
                return;
            }

            Log.Information($"FOUND Grab Event: ID {grabEvent.Id}, Title: {grabEvent.SourceTitle}, EventType: {grabEvent.EventType}");

            // 3. Attempt Blacklist
            Log.Information("Attempting to blacklist (Mark as Failed)...");
            var success = await client.MarkHistoryFailedAsync(grabEvent.Id);

            if (success)
            {
                Log.Information("SUCCESS: History item marked as failed.");
            }
            else
            {
                Log.Error("FAILURE: Failed to mark history item as failed.");
            }

        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred during testing.");
        }
    }
}