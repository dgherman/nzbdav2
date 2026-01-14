using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Tools;

public class MockBenchmark
{
    public static async Task RunAsync(string[] args)
    {
        Log.Information("Starting Mock Benchmark...");

        // Use test database in local_data
        Environment.SetEnvironmentVariable("CONFIG_PATH", "local_data");
        // Disable smart analysis for benchmark
        Environment.SetEnvironmentVariable("BENCHMARK", "true");

        // Ensure directory exists
        if (!Directory.Exists("local_data")) Directory.CreateDirectory("local_data");

        // Run migrations
        Log.Information("Ensuring test database is migrated...");
        await using (var dbMigrate = new DavDatabaseContext())
        {
            await dbMigrate.Database.MigrateAsync();
        }

        var port = 1190;
        var latencyMs = 150;
        var jitterMs = 40;
        var timeoutRate = 0.01; // 1% chance of stall
        var useRar = false;
        var rarVolumes = 3;

        // Check args for options
        foreach (var arg in args)
        {
            if (arg.StartsWith("--latency=")) int.TryParse(arg.Substring(10), out latencyMs);
            if (arg.StartsWith("--jitter=")) int.TryParse(arg.Substring(9), out jitterMs);
            if (arg.StartsWith("--timeout-rate=")) double.TryParse(arg.Substring(15), out timeoutRate);
            if (arg == "--rar") useRar = true;
            if (arg.StartsWith("--rar-volumes=")) int.TryParse(arg.Substring(14), out rarVolumes);
        }

        var segmentSize = 700 * 1024;

        // 1. Start Mock Server
        using var server = new MockNntpServer(port, latencyMs, segmentSize, jitterMs, timeoutRate);
        server.Start();
        Log.Information($"Mock NNTP Server started on port {port} with {latencyMs}ms latency (Jitter: {jitterMs}ms, TimeoutRate: {timeoutRate:P1}).");

        // 2. Generate Mock NZB
        var nzbPath = "mock.nzb";
        var totalSize = 200L * 1024 * 1024; // 200 MB

        if (useRar)
        {
            // Use single RAR file to avoid multipart grouping complexity in the test
            var result = await MockNzbGenerator.GenerateAsync(nzbPath, totalSize, segmentSize, useRar: true, rarVolumeCount: 1);
            Log.Information($"Generated RAR NZB: {nzbPath} ({totalSize / 1024 / 1024} MB, 1 volume, {result.TotalSegments} segments).");
            foreach (var file in result.Files)
            {
                Log.Information($"  - {file.FileName}: {file.SegmentCount} segments");
            }
        }
        else
        {
            await MockNzbGenerator.GenerateAsync(nzbPath, totalSize, segmentSize);
            Log.Information($"Generated flat file NZB: {nzbPath} ({totalSize / 1024 / 1024} MB).");
        }

        // 3. Inject Config
        await using var db = new DavDatabaseContext();
        var configProvider = await db.ConfigItems.AsNoTracking().FirstOrDefaultAsync(x => x.ConfigName == "usenet.providers");
        var configConns = await db.ConfigItems.AsNoTracking().FirstOrDefaultAsync(x => x.ConfigName == "usenet.connections-per-stream");

        var originalProviderValue = configProvider?.ConfigValue;
        var originalConnsValue = configConns?.ConfigValue;

        try
        {
            var mockConfig = new UsenetProviderConfig
            {
                Providers = new List<UsenetProviderConfig.ConnectionDetails>
                {
                    new() {
                        Type = ProviderType.Pooled,
                        Host = "127.0.0.1",
                        Port = port,
                        UseSsl = false,
                        User = "mock",
                        Pass = "mock",
                        MaxConnections = 20
                    }
                }
            };

            var json = JsonSerializer.Serialize(mockConfig);

            // Update Providers
            var pItem = await db.ConfigItems.FirstOrDefaultAsync(x => x.ConfigName == "usenet.providers");
            if (pItem == null) { pItem = new ConfigItem { ConfigName = "usenet.providers" }; db.ConfigItems.Add(pItem); }
            pItem.ConfigValue = json;

            // Update Connections
            var cItem = await db.ConfigItems.FirstOrDefaultAsync(x => x.ConfigName == "usenet.connections-per-stream");
            if (cItem == null) { cItem = new ConfigItem { ConfigName = "usenet.connections-per-stream" }; db.ConfigItems.Add(cItem); }
            cItem.ConfigValue = "16"; // Sweet spot for mock server throughput (diminishing returns > 16)

            // Limit Queue and Repair to reserve connections for Streaming
            // Total 20. Queue=2, Repair=2 => Streaming=16
            var qItem = await db.ConfigItems.FirstOrDefaultAsync(x => x.ConfigName == "api.max-queue-connections");
            if (qItem == null) { qItem = new ConfigItem { ConfigName = "api.max-queue-connections" }; db.ConfigItems.Add(qItem); }
            qItem.ConfigValue = "2";

            var rItem = await db.ConfigItems.FirstOrDefaultAsync(x => x.ConfigName == "repair.connections");
            if (rItem == null) { rItem = new ConfigItem { ConfigName = "repair.connections" }; db.ConfigItems.Add(rItem); }
            rItem.ConfigValue = "2";

            await db.SaveChangesAsync();
            Log.Information("Injected Mock Provider Config.");

            // 4. Run Benchmark
            Log.Information("Running FullNzbTester...");
            var testArgs = useRar
                ? new[] { "--test-full-nzb", nzbPath, "--skip-rar-parsing" }
                : new[] { "--test-full-nzb", nzbPath };
            await FullNzbTester.RunAsync(testArgs);

        }
        catch (Exception ex)
        {
            Log.Error(ex, "Benchmark failed.");
        }
        finally
        {
            // 5. Restore Config
            Log.Information("Restoring Config...");
            try
            {
                // Create new context to avoid tracking issues
                await using var dbRestore = new DavDatabaseContext();

                if (originalProviderValue != null)
                {
                    var p = await dbRestore.ConfigItems.FirstOrDefaultAsync(x => x.ConfigName == "usenet.providers");
                    if (p != null) p.ConfigValue = originalProviderValue;
                }

                if (originalConnsValue != null)
                {
                    var c = await dbRestore.ConfigItems.FirstOrDefaultAsync(x => x.ConfigName == "usenet.connections-per-stream");
                    if (c != null) c.ConfigValue = originalConnsValue;
                }

                await dbRestore.SaveChangesAsync();
                Log.Information("Config Restored.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to restore config.");
            }
        }
    }
}
