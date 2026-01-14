using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Tools;

public class NzbFromDbTester
{
    public static async Task RunAsync(string[] args)
    {
        var argIndex = args.ToList().IndexOf("--test-db-nzb");

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  DATABASE NZB PERFORMANCE TESTER");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // Parse arguments
        var searchPattern = "";
        var downloadSize = 0L; // 0 = full file
        var connections = 20;

        for (int i = argIndex + 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--search=")) searchPattern = arg.Substring(9);
            else if (arg.StartsWith("--size=")) downloadSize = long.Parse(arg.Substring(7)) * 1024 * 1024;
            else if (arg.StartsWith("--connections=")) connections = int.Parse(arg.Substring(14));
            else if (!arg.StartsWith("--") && string.IsNullOrEmpty(searchPattern)) searchPattern = arg;
        }

        if (string.IsNullOrEmpty(searchPattern))
        {
            Console.WriteLine("\nUsage: --test-db-nzb [options] <search-pattern>");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  --search=<pattern>     Search pattern for file name (SQL LIKE)");
            Console.WriteLine("  --size=<MB>            Download only first N MB (default: full file)");
            Console.WriteLine("  --connections=<N>      Connections per stream (default: 20)");
            Console.WriteLine("\nExamples:");
            Console.WriteLine("  --test-db-nzb \"Emily%Paris%S05E01%\"");
            Console.WriteLine("  --test-db-nzb --search=\"Tell.Me.Lies%\" --size=100");
            return;
        }

        // Enable detailed timing
        BufferedSegmentStream.EnableDetailedTiming = true;
        BufferedSegmentStream.ResetGlobalTimingStats();

        // Initialize services
        var services = new ServiceCollection();
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);

        // Override connections per stream
        configManager.UpdateValues(new List<Database.Models.ConfigItem>
        {
            new() { ConfigName = "usenet.connections-per-stream", ConfigValue = connections.ToString() }
        });

        // Override total streaming connections for testing
        configManager.UpdateValues(new List<Database.Models.ConfigItem>
        {
            new() { ConfigName = "usenet.total-streaming-connections", ConfigValue = connections.ToString() }
        });

        services.AddSingleton(configManager);
        services.AddSingleton<WebsocketManager>();
        services.AddSingleton<BandwidthService>();
        services.AddSingleton<ProviderErrorService>();
        services.AddSingleton<NzbProviderAffinityService>();
        services.AddSingleton<StreamingConnectionLimiter>();  // Global streaming connection limiter
        services.AddSingleton<UsenetStreamingClient>();
        services.AddDbContext<DavDatabaseContext>();

        var sp = services.BuildServiceProvider();
        // Initialize the limiter (sets static Instance)
        _ = sp.GetRequiredService<StreamingConnectionLimiter>();
        var client = sp.GetRequiredService<UsenetStreamingClient>();

        try
        {
            // Find file in database
            await using var db = new DavDatabaseContext();

            var query = from item in db.Items
                        join nzb in db.NzbFiles on item.Id equals nzb.Id
                        where EF.Functions.Like(item.Name, $"%{searchPattern}%")
                        orderby item.FileSize descending
                        select new { item.Id, item.Name, item.FileSize, NzbFile = nzb };

            var file = await query.FirstOrDefaultAsync().ConfigureAwait(false);

            if (file == null)
            {
                Console.WriteLine($"ERROR: No file found matching pattern: {searchPattern}");
                return;
            }

            Console.WriteLine($"  File: {file.Name}");
            Console.WriteLine($"  Size: {file.FileSize / 1024.0 / 1024.0:F2} MB");
            Console.WriteLine($"  Connections: {connections}");
            Console.WriteLine($"  Download Size: {(downloadSize > 0 ? $"{downloadSize / 1024 / 1024} MB" : "Full file")}");
            Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

            // Get segment IDs from the NzbFile entity
            var segmentIds = file.NzbFile.SegmentIds;
            if (segmentIds == null || segmentIds.Length == 0)
            {
                Console.WriteLine("ERROR: No segment IDs found");
                return;
            }

            Console.WriteLine($"Segments: {segmentIds.Length}");

            // Calculate target size
            var fileSize = file.FileSize ?? 0;
            var targetSize = downloadSize > 0 ? Math.Min(downloadSize, fileSize) : fileSize;

            // Create buffered stream using UsenetStreamingClient.GetFileStream
            Console.WriteLine("\n--- SEQUENTIAL THROUGHPUT BENCHMARK ---");
            Console.WriteLine($"Downloading {targetSize / 1024.0 / 1024.0:F1} MB...\n");

            var usageContext = new ConnectionUsageContext(ConnectionUsageType.Streaming);
            var stream = client.GetFileStream(
                segmentIds,
                fileSize,
                connections,
                usageContext,
                useBufferedStreaming: true,
                bufferSize: connections * 5
            );

            var buffer = new byte[256 * 1024]; // 256KB chunks
            var totalRead = 0L;
            var readCount = 0;
            var readTimes = new List<double>();
            var lastProgressMb = 0L;

            var overallWatch = Stopwatch.StartNew();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            while (totalRead < targetSize && !cts.Token.IsCancellationRequested)
            {
                var readWatch = Stopwatch.StartNew();
                var bytesToRead = (int)Math.Min(buffer.Length, targetSize - totalRead);
                var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cts.Token).ConfigureAwait(false);
                readWatch.Stop();

                if (read == 0) break;

                totalRead += read;
                readCount++;
                readTimes.Add(readWatch.Elapsed.TotalMilliseconds);

                // Progress every 10MB
                var currentMb = totalRead / (10 * 1024 * 1024);
                if (currentMb > lastProgressMb)
                {
                    var currentSpeed = (totalRead / 1024.0 / 1024.0) / overallWatch.Elapsed.TotalSeconds;
                    Console.WriteLine($"  Progress: {totalRead / 1024.0 / 1024.0:F1} MB @ {currentSpeed:F2} MB/s");
                    lastProgressMb = currentMb;
                }
            }

            overallWatch.Stop();
            await stream.DisposeAsync().ConfigureAwait(false);

            // Statistics
            var totalMb = totalRead / 1024.0 / 1024.0;
            var totalSeconds = overallWatch.Elapsed.TotalSeconds;
            var speed = totalMb / totalSeconds;

            if (readTimes.Count > 0)
            {
                readTimes.Sort();
                var avgReadTime = readTimes.Average();
                var medianReadTime = readTimes[readTimes.Count / 2];
                var p95ReadTime = readTimes[(int)(readTimes.Count * 0.95)];
                var maxReadTime = readTimes.Max();
                var minReadTime = readTimes.Min();

                Console.WriteLine($"\n═══════════════════════════════════════════════════════════════");
                Console.WriteLine($"  RESULTS SUMMARY");
                Console.WriteLine($"═══════════════════════════════════════════════════════════════");
                Console.WriteLine($"  Total Downloaded: {totalMb:F2} MB in {totalSeconds:F2}s");
                Console.WriteLine($"  Speed:            {speed:F2} MB/s");
                Console.WriteLine($"───────────────────────────────────────────────────────────────");
                Console.WriteLine($"  Read Statistics ({readCount} reads):");
                Console.WriteLine($"    Avg:    {avgReadTime:F2}ms");
                Console.WriteLine($"    Median: {medianReadTime:F2}ms");
                Console.WriteLine($"    P95:    {p95ReadTime:F2}ms");
                Console.WriteLine($"    Min/Max: {minReadTime:F2}ms / {maxReadTime:F2}ms");
                Console.WriteLine($"───────────────────────────────────────────────────────────────");

                // Bottleneck analysis
                Console.WriteLine("  Bottleneck Analysis:");
                if (p95ReadTime > 100)
                {
                    Console.WriteLine($"    WARNING: P95 read time ({p95ReadTime:F0}ms) > 100ms - buffer starvation");
                }
                if (maxReadTime > 1000)
                {
                    Console.WriteLine($"    WARNING: Max read time ({maxReadTime:F0}ms) > 1s - stall detected");
                }
                if (speed < 10)
                {
                    Console.WriteLine($"    WARNING: Low throughput ({speed:F1} MB/s) - check provider connections");
                }
                if (p95ReadTime <= 100 && maxReadTime <= 1000 && speed >= 10)
                {
                    Console.WriteLine("    OK: No obvious bottlenecks detected");
                }
            }

            // Print timing breakdown
            if (BufferedSegmentStream.EnableDetailedTiming)
            {
                BufferedSegmentStream.GetGlobalTimingStats().Print();
            }

            Console.WriteLine($"═══════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
