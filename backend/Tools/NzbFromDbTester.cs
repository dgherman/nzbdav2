using System;
using System.IO;
using System.Linq;
using System.Text;
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
using NzbWebDAV.Extensions;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;
using Serilog;
using Usenet.Nzb;

namespace NzbWebDAV.Tools;

public class NzbFromDbTester
{
    public static async Task RunAsync(string[] args)
    {
        var argIndex = args.ToList().IndexOf("--test-db-nzb");

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  NZB TESTER");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // Parse arguments
        var searchPattern = "";
        var nzbFilePath = "";
        var downloadSize = 0L; // 0 = full file
        var connections = 20;
        var importOnly = false;
        var healthCheck = false;
        var findMode = false;
        var testImport = false;
        int? forcedProvider = null;  // Force using a specific provider by index

        for (int i = argIndex + 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--search=")) searchPattern = arg.Substring(9);
            else if (arg.StartsWith("--file=")) nzbFilePath = arg.Substring(7);
            else if (arg.StartsWith("--size=")) downloadSize = long.Parse(arg.Substring(7)) * 1024 * 1024;
            else if (arg.StartsWith("--connections=")) connections = int.Parse(arg.Substring(14));
            else if (arg.StartsWith("--provider=")) forcedProvider = int.Parse(arg.Substring(11));
            else if (arg == "--import-only") importOnly = true;
            else if (arg == "--health-check") healthCheck = true;
            else if (arg == "--find") findMode = true;
            else if (arg == "--test-import") testImport = true;
            else if (!arg.StartsWith("--") && string.IsNullOrEmpty(searchPattern)) searchPattern = arg;
        }

        // Handle --find mode to list matching files
        if (findMode && !string.IsNullOrEmpty(searchPattern))
        {
            await ListMatchingFiles(searchPattern).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(searchPattern) && string.IsNullOrEmpty(nzbFilePath))
        {
            PrintUsage();
            return;
        }

        // Handle --test-import mode for import timing analysis
        if (testImport)
        {
            await RunImportTest(nzbFilePath, searchPattern, connections).ConfigureAwait(false);
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

        // Override queue and healthcheck connections to 0 for streaming-only tests
        // This ensures GlobalOperationLimiter gives all connections to streaming
        // (otherwise streaming = total - queue - healthcheck = 1 if queue+healthcheck > total)
        configManager.UpdateValues(new List<Database.Models.ConfigItem>
        {
            new() { ConfigName = "api.max-queue-connections", ConfigValue = "0" },
            new() { ConfigName = "repair.connections", ConfigValue = "0" }
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
            string fileName;
            string[] segmentIds;
            long fileSize;

            // Load NZB from file or database
            if (!string.IsNullOrEmpty(nzbFilePath))
            {
                // Load from disk
                var result = await LoadNzbFromFile(nzbFilePath).ConfigureAwait(false);
                if (result == null) return;
                (fileName, segmentIds, fileSize) = result.Value;
            }
            else
            {
                // Load from database
                var result = await LoadNzbFromDatabase(searchPattern).ConfigureAwait(false);
                if (result == null) return;
                (fileName, segmentIds, fileSize) = result.Value;
            }

            // Get provider info for display
            var providers = client.GetProviderInfo();

            Console.WriteLine($"  File: {fileName}");
            Console.WriteLine($"  Size: {fileSize / 1024.0 / 1024.0:F2} MB");
            Console.WriteLine($"  Segments: {segmentIds.Length}");
            Console.WriteLine($"  Connections: {connections}");
            Console.WriteLine("───────────────────────────────────────────────────────────────");
            Console.WriteLine("  Providers:");
            foreach (var (index, host, type, maxConnections) in providers)
            {
                var marker = forcedProvider.HasValue && forcedProvider.Value == index ? " ← FORCED" : "";
                var typeLabel = type == "Pooled" ? "" : $" [{type}]";
                Console.WriteLine($"    [{index}] {host}{typeLabel} ({maxConnections} conn){marker}");
            }
            if (forcedProvider.HasValue)
            {
                var forcedHost = providers.FirstOrDefault(p => p.Index == forcedProvider.Value).Host ?? "unknown";
                Console.WriteLine($"  Mode: SINGLE PROVIDER ({forcedHost})");
            }
            else
            {
                Console.WriteLine($"  Mode: ALL PROVIDERS (load balanced)");
            }
            if (!importOnly)
            {
                Console.WriteLine($"  Download Size: {(downloadSize > 0 ? $"{downloadSize / 1024 / 1024} MB" : "Full file")}");
            }
            Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

            // Import-only mode: just parse and validate, no streaming
            if (importOnly)
            {
                Console.WriteLine("--- IMPORT VALIDATION COMPLETE ---");
                Console.WriteLine($"  Successfully parsed NZB with {segmentIds.Length} segments");
                Console.WriteLine($"  Estimated file size: {fileSize / 1024.0 / 1024.0:F2} MB");
                Console.WriteLine("═══════════════════════════════════════════════════════════════");
                return;
            }

            // Health check mode: check segment availability
            if (healthCheck)
            {
                await RunHealthCheck(segmentIds, fileName, client, connections).ConfigureAwait(false);
                return;
            }

            // Calculate target size
            var targetSize = downloadSize > 0 ? Math.Min(downloadSize, fileSize) : fileSize;

            // Create buffered stream using UsenetStreamingClient.GetFileStream
            Console.WriteLine("\n--- SEQUENTIAL THROUGHPUT BENCHMARK ---");
            Console.WriteLine($"Downloading {targetSize / 1024.0 / 1024.0:F1} MB...\n");

            // Create usage context with AffinityKey for proper provider affinity tracking
            // AffinityKey is typically the normalized parent directory name (job name)
            var affinityKey = FilenameNormalizer.NormalizeName(Path.GetFileNameWithoutExtension(fileName));
            var usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Streaming,
                new ConnectionUsageDetails
                {
                    Text = fileName,
                    JobName = fileName,
                    AffinityKey = affinityKey,
                    ForcedProviderIndex = forcedProvider
                }
            );
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

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -- --test-db-nzb [options] [search-pattern]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  <search-pattern>        Search for file in database by name");
        Console.WriteLine("  --file=<path>           Load NZB from disk file instead of database");
        Console.WriteLine("  --find                  List matching files without testing");
        Console.WriteLine("  --size=<MB>             Download only first N MB (default: full file)");
        Console.WriteLine("  --connections=<N>       Number of connections to use (default: 20)");
        Console.WriteLine("  --provider=<N>          Force using provider index N (0-based, bypasses affinity)");
        Console.WriteLine("  --import-only           Parse and validate NZB only, no streaming");
        Console.WriteLine("  --health-check          Check segment availability on providers");
        Console.WriteLine("  --test-import           Benchmark import pipeline with timing breakdown");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- --test-db-nzb \"Movie.2024\"                    # Test from DB");
        Console.WriteLine("  dotnet run -- --test-db-nzb --find \"Movie.2024\"             # List matches");
        Console.WriteLine("  dotnet run -- --test-db-nzb --file=/path/to/file.nzb        # From disk");
        Console.WriteLine("  dotnet run -- --test-db-nzb --file=/path/to.nzb --import-only");
        Console.WriteLine("  dotnet run -- --test-db-nzb --file=/path/to.nzb --test-import");
        Console.WriteLine("  dotnet run -- --test-db-nzb \"Movie.2024\" --health-check");
        Console.WriteLine("  dotnet run -- --test-db-nzb \"Movie.2024\" --size=100 --connections=30");
        Console.WriteLine("  dotnet run -- --test-db-nzb \"Movie.2024\" --provider=0       # Test provider 0 only");
        Console.WriteLine("  dotnet run -- --test-db-nzb \"Movie.2024\" --provider=1       # Test provider 1 only");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private static async Task ListMatchingFiles(string searchPattern)
    {
        Console.WriteLine($"  Searching for: {searchPattern}");
        Console.WriteLine("───────────────────────────────────────────────────────────────");

        await using var db = new DavDatabaseContext();
        var matches = await db.NzbFiles
            .Include(n => n.DavItem)
            .Where(n => n.DavItem != null && n.DavItem.Name.Contains(searchPattern))
            .OrderByDescending(n => n.DavItem!.FileSize)
            .Take(20)
            .Select(n => new { n.DavItem!.Name, n.DavItem.FileSize, n.DavItem.Id })
            .ToListAsync()
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            Console.WriteLine("  No matching files found.");
        }
        else
        {
            Console.WriteLine($"  Found {matches.Count} matches:");
            foreach (var match in matches)
            {
                var sizeMb = (match.FileSize ?? 0) / 1024.0 / 1024.0;
                var sizeStr = sizeMb >= 1024 ? $"{sizeMb / 1024:F2} GB" : $"{sizeMb:F2} MB";
                Console.WriteLine($"    {sizeStr,10}  {match.Name}");
            }
        }
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private static async Task<(string FileName, string[] SegmentIds, long FileSize)?> LoadNzbFromFile(string filePath)
    {
        Console.WriteLine($"  Loading NZB from: {filePath}");

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"  ERROR: File not found: {filePath}");
            return null;
        }

        try
        {
            await using var fileStream = File.OpenRead(filePath);
            var nzbDoc = NzbDocument.Load(fileStream);

            if (nzbDoc.Files.Count == 0)
            {
                Console.WriteLine("  ERROR: NZB contains no files");
                return null;
            }

            // Find the largest file (likely the main content)
            var largestFile = nzbDoc.Files.OrderByDescending(f => f.Size).First();
            var fileName = largestFile.FileName;

            // Extract segment IDs (convert NntpMessageId to string)
            var segmentIds = largestFile.Segments
                .OrderBy(s => s.Number)
                .Select(s => s.MessageId.Value)
                .ToArray();

            var fileSize = largestFile.Size;

            Console.WriteLine($"  NZB contains {nzbDoc.Files.Count} file(s)");
            Console.WriteLine($"  Selected largest: {fileName}");
            Console.WriteLine("───────────────────────────────────────────────────────────────");

            return (fileName, segmentIds, fileSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR parsing NZB: {ex.Message}");
            return null;
        }
    }

    private static async Task<(string FileName, string[] SegmentIds, long FileSize)?> LoadNzbFromDatabase(string searchPattern)
    {
        Console.WriteLine($"  Loading from database: {searchPattern}");

        await using var db = new DavDatabaseContext();

        // Find matching NZB file
        var file = await db.NzbFiles
            .Include(n => n.DavItem)
            .Where(n => n.DavItem != null && n.DavItem.Name.Contains(searchPattern))
            .OrderByDescending(n => n.DavItem!.FileSize)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (file != null)
        {
            var fileName = file.DavItem?.Name ?? "Unknown";
            var fileSize = file.DavItem?.FileSize ?? 0;
            var segmentIds = file.SegmentIds;

            if (segmentIds.Length == 0)
            {
                Console.WriteLine("  ERROR: No segments found for this file");
                return null;
            }

            Console.WriteLine($"  Type: NzbFile");
            Console.WriteLine("───────────────────────────────────────────────────────────────");
            return (fileName, segmentIds, fileSize);
        }

        // Try MultipartFile if no NzbFile found
        var multipartFile = await db.MultipartFiles
            .Include(m => m.DavItem)
            .Where(m => m.DavItem != null && m.DavItem.Name.Contains(searchPattern))
            .OrderByDescending(m => m.DavItem!.FileSize)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (multipartFile != null)
        {
            var fileName = multipartFile.DavItem?.Name ?? "Unknown";
            var fileSize = multipartFile.DavItem?.FileSize ?? 0;
            var segmentIds = multipartFile.Metadata?.FileParts?
                .SelectMany(p => p.SegmentIds)
                .ToArray() ?? [];

            if (segmentIds.Length == 0)
            {
                Console.WriteLine("  ERROR: No segments found for this multipart file");
                return null;
            }

            Console.WriteLine($"  Type: MultipartFile ({multipartFile.Metadata?.FileParts?.Count() ?? 0} parts)");
            Console.WriteLine("───────────────────────────────────────────────────────────────");
            return (fileName, segmentIds, fileSize);
        }

        // Try RarFile if no MultipartFile found
        var rarFile = await db.RarFiles
            .Include(r => r.DavItem)
            .Where(r => r.DavItem != null && r.DavItem.Name.Contains(searchPattern))
            .OrderByDescending(r => r.DavItem!.FileSize)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (rarFile != null)
        {
            var fileName = rarFile.DavItem?.Name ?? "Unknown";
            var fileSize = rarFile.DavItem?.FileSize ?? 0;
            var segmentIds = rarFile.RarParts?
                .SelectMany(p => p.SegmentIds)
                .ToArray() ?? [];

            if (segmentIds.Length == 0)
            {
                Console.WriteLine("  ERROR: No segments found for this RAR file");
                return null;
            }

            Console.WriteLine($"  Type: RarFile ({rarFile.RarParts?.Count() ?? 0} parts)");
            Console.WriteLine("───────────────────────────────────────────────────────────────");
            return (fileName, segmentIds, fileSize);
        }

        Console.WriteLine($"  ERROR: No file found matching '{searchPattern}'");
        Console.WriteLine("  Use --find to list matching files");
        return null;
    }

    private static async Task RunHealthCheck(string[] segmentIds, string fileName, UsenetStreamingClient client, int connections)
    {
        Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  HEALTH CHECK TIMING ANALYSIS");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  File: {fileName}");
        Console.WriteLine($"  Total Segments: {segmentIds.Length}");
        Console.WriteLine($"  Connections: {connections}");
        Console.WriteLine("───────────────────────────────────────────────────────────────\n");

        var overallWatch = Stopwatch.StartNew();
        var segmentTimes = new List<double>();
        var batchTimes = new List<double>();
        var batchSizes = new List<int>();
        var lastBatchTime = Stopwatch.StartNew();
        var lastBatchCount = 0;
        var processedCount = 0;
        var timeoutCount = 0;
        var lastProgressReport = 0;

        // Track throughput over time
        var throughputSamples = new List<(double elapsed, double segmentsPerSec)>();
        var sampleInterval = Math.Max(50, segmentIds.Length / 20); // ~20 samples

        var progress = new Progress<int>(checked_ =>
        {
            var now = overallWatch.Elapsed.TotalSeconds;
            var batchElapsed = lastBatchTime.Elapsed.TotalMilliseconds;
            var batchCount = checked_ - lastBatchCount;

            if (batchCount > 0)
            {
                var msPerSegment = batchElapsed / batchCount;
                for (int i = 0; i < batchCount; i++)
                    segmentTimes.Add(msPerSegment);

                batchTimes.Add(batchElapsed);
                batchSizes.Add(batchCount);
            }

            processedCount = checked_;
            lastBatchCount = checked_;
            lastBatchTime.Restart();

            // Sample throughput
            if (checked_ - lastProgressReport >= sampleInterval || checked_ == segmentIds.Length)
            {
                var segmentsPerSec = checked_ / now;
                throughputSamples.Add((now, segmentsPerSec));

                var pct = 100.0 * checked_ / segmentIds.Length;
                var eta = segmentsPerSec > 0 ? (segmentIds.Length - checked_) / segmentsPerSec : 0;
                Console.WriteLine($"  Progress: {checked_,6}/{segmentIds.Length} ({pct,5:F1}%) | {segmentsPerSec,6:F1} seg/s | ETA: {eta:F0}s");
                lastProgressReport = checked_;
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var usageContext = new ConnectionUsageContext(ConnectionUsageType.HealthCheck);
        using var _ = cts.Token.SetScopedContext(usageContext);

        var status = "HEALTHY";
        var statusMessage = "All segments are available";
        string? missingSegment = null;

        try
        {
            // Use STAT (useHead: false) for faster checking
            await client.CheckAllSegmentsAsync(segmentIds, connections, progress, cts.Token, useHead: false).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException ex)
        {
            status = "UNHEALTHY";
            statusMessage = "Missing segment detected";
            missingSegment = ex.SegmentId;
        }
        catch (TimeoutException ex)
        {
            status = "TIMEOUT";
            statusMessage = ex.Message;
            timeoutCount++;
        }
        catch (OperationCanceledException)
        {
            status = "CANCELLED";
            statusMessage = "Check was cancelled or timed out";
        }

        overallWatch.Stop();
        var totalSeconds = overallWatch.Elapsed.TotalSeconds;

        // Calculate statistics
        Console.WriteLine($"\n═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  TIMING BREAKDOWN");
        Console.WriteLine($"═══════════════════════════════════════════════════════════════");

        Console.WriteLine($"  Total Time: {totalSeconds:F2}s");
        Console.WriteLine($"  Segments Checked: {processedCount}/{segmentIds.Length}");
        Console.WriteLine($"  Overall Throughput: {processedCount / totalSeconds:F1} segments/sec");
        Console.WriteLine($"  Theoretical Max: {connections * 1000.0 / 50:F0} seg/s (assuming 50ms/segment)");

        if (segmentTimes.Count > 0)
        {
            segmentTimes.Sort();
            var avg = segmentTimes.Average();
            var p50 = segmentTimes[segmentTimes.Count / 2];
            var p90 = segmentTimes[(int)(segmentTimes.Count * 0.90)];
            var p95 = segmentTimes[(int)(segmentTimes.Count * 0.95)];
            var p99 = segmentTimes[(int)(segmentTimes.Count * 0.99)];
            var max = segmentTimes.Max();
            var min = segmentTimes.Min();

            Console.WriteLine($"\n───────────────────────────────────────────────────────────────");
            Console.WriteLine($"  Per-Segment Latency (ms):");
            Console.WriteLine($"    Min:    {min,8:F2}ms");
            Console.WriteLine($"    Avg:    {avg,8:F2}ms");
            Console.WriteLine($"    P50:    {p50,8:F2}ms");
            Console.WriteLine($"    P90:    {p90,8:F2}ms");
            Console.WriteLine($"    P95:    {p95,8:F2}ms");
            Console.WriteLine($"    P99:    {p99,8:F2}ms");
            Console.WriteLine($"    Max:    {max,8:F2}ms");

            // Latency histogram
            Console.WriteLine($"\n  Latency Distribution:");
            var buckets = new[] { 10, 25, 50, 100, 200, 500, 1000, 5000 };
            var counts = new int[buckets.Length + 1];
            foreach (var t in segmentTimes)
            {
                var idx = Array.FindIndex(buckets, b => t <= b);
                counts[idx >= 0 ? idx : buckets.Length]++;
            }
            for (int i = 0; i < buckets.Length; i++)
            {
                var pct = 100.0 * counts[i] / segmentTimes.Count;
                var bar = new string('█', (int)(pct / 2));
                var label = i == 0 ? $"<{buckets[i]}ms" : $"{buckets[i - 1]}-{buckets[i]}ms";
                Console.WriteLine($"    {label,-12} {counts[i],6} ({pct,5:F1}%) {bar}");
            }
            if (counts[buckets.Length] > 0)
            {
                var pct = 100.0 * counts[buckets.Length] / segmentTimes.Count;
                var bar = new string('█', (int)(pct / 2));
                Console.WriteLine($"    {">" + buckets[^1] + "ms",-12} {counts[buckets.Length],6} ({pct,5:F1}%) {bar}");
            }
        }

        if (batchTimes.Count > 0)
        {
            Console.WriteLine($"\n───────────────────────────────────────────────────────────────");
            Console.WriteLine($"  Batch Statistics ({batchTimes.Count} batches):");
            batchTimes.Sort();
            var avgBatch = batchTimes.Average();
            var p95Batch = batchTimes[(int)(batchTimes.Count * 0.95)];
            Console.WriteLine($"    Avg Batch Time: {avgBatch:F2}ms");
            Console.WriteLine($"    P95 Batch Time: {p95Batch:F2}ms");
            Console.WriteLine($"    Max Batch Time: {batchTimes.Max():F2}ms");
        }

        // Throughput over time
        if (throughputSamples.Count > 2)
        {
            Console.WriteLine($"\n───────────────────────────────────────────────────────────────");
            Console.WriteLine($"  Throughput Timeline:");
            foreach (var (elapsed, rate) in throughputSamples)
            {
                var bar = new string('█', (int)(rate / 10));
                Console.WriteLine($"    {elapsed,6:F1}s: {rate,6:F1} seg/s {bar}");
            }

            // Check for degradation
            if (throughputSamples.Count >= 3)
            {
                var firstThird = throughputSamples.Take(throughputSamples.Count / 3).Average(x => x.segmentsPerSec);
                var lastThird = throughputSamples.Skip(2 * throughputSamples.Count / 3).Average(x => x.segmentsPerSec);
                var degradation = (firstThird - lastThird) / firstThird * 100;
                if (degradation > 20)
                {
                    Console.WriteLine($"\n  ⚠️  THROUGHPUT DEGRADATION: {degradation:F0}% slowdown detected");
                    Console.WriteLine($"      First third: {firstThird:F1} seg/s, Last third: {lastThird:F1} seg/s");
                }
            }
        }

        // Bottleneck Analysis
        Console.WriteLine($"\n───────────────────────────────────────────────────────────────");
        Console.WriteLine($"  BOTTLENECK ANALYSIS:");

        var actualThroughput = processedCount / totalSeconds;
        var theoreticalMax = connections * 20.0; // Assuming 50ms per segment = 20/sec per connection

        if (actualThroughput < theoreticalMax * 0.5)
        {
            Console.WriteLine($"  ⚠️  Throughput ({actualThroughput:F0} seg/s) is <50% of theoretical max ({theoreticalMax:F0} seg/s)");
            Console.WriteLine($"      Possible causes:");
            Console.WriteLine($"      - Provider latency (check P95/P99 times)");
            Console.WriteLine($"      - Connection pool contention");
            Console.WriteLine($"      - Network bandwidth limitation");
        }

        if (segmentTimes.Count > 0)
        {
            var p99 = segmentTimes[(int)(segmentTimes.Count * 0.99)];
            var p50 = segmentTimes[segmentTimes.Count / 2];

            if (p99 > p50 * 10)
            {
                Console.WriteLine($"  ⚠️  High latency variance: P99 ({p99:F0}ms) is {p99 / p50:F1}x higher than P50 ({p50:F0}ms)");
                Console.WriteLine($"      This indicates inconsistent provider response times");
            }

            if (p50 > 100)
            {
                Console.WriteLine($"  ⚠️  High median latency: P50 = {p50:F0}ms (should be <100ms)");
                Console.WriteLine($"      Consider using a provider with lower latency");
            }
        }

        if (actualThroughput >= theoreticalMax * 0.7 && segmentTimes.Count > 0 && segmentTimes[(int)(segmentTimes.Count * 0.95)] < 200)
        {
            Console.WriteLine($"  ✓  No obvious bottlenecks detected");
            Console.WriteLine($"      Throughput is {100 * actualThroughput / theoreticalMax:F0}% of theoretical max");
        }

        // Final Status
        Console.WriteLine($"\n═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  STATUS: {status}");
        Console.WriteLine($"  {statusMessage}");
        if (missingSegment != null)
        {
            var idx = Array.IndexOf(segmentIds, missingSegment);
            var pct = 100.0 * idx / segmentIds.Length;
            Console.WriteLine($"  Missing at: {idx}/{segmentIds.Length} ({pct:F1}%)");
            Console.WriteLine($"  Segment ID: {missingSegment}");
        }
        Console.WriteLine($"═══════════════════════════════════════════════════════════════");
    }

    private static async Task RunImportTest(string nzbFilePath, string searchPattern, int connections)
    {
        Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  IMPORT PIPELINE TIMING TEST");
        Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

        // Initialize services
        var services = new ServiceCollection();
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);

        // Override connections for testing
        configManager.UpdateValues(new List<Database.Models.ConfigItem>
        {
            new() { ConfigName = "usenet.connections-per-stream", ConfigValue = connections.ToString() },
            new() { ConfigName = "usenet.total-streaming-connections", ConfigValue = connections.ToString() },
            new() { ConfigName = "api.max-queue-connections", ConfigValue = connections.ToString() }
        });

        services.AddSingleton(configManager);
        services.AddSingleton<WebsocketManager>();
        services.AddSingleton<BandwidthService>();
        services.AddSingleton<ProviderErrorService>();
        services.AddSingleton<NzbProviderAffinityService>();
        services.AddSingleton<StreamingConnectionLimiter>();
        services.AddSingleton<UsenetStreamingClient>();
        services.AddDbContext<DavDatabaseContext>();

        var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<StreamingConnectionLimiter>();
        var client = sp.GetRequiredService<UsenetStreamingClient>();

        // Timing storage
        var timings = new Dictionary<string, double>();
        var stepDetails = new Dictionary<string, string>();
        var overallWatch = Stopwatch.StartNew();

        try
        {
            // Step 0: Load and parse NZB
            Console.WriteLine("Step 0: Loading and parsing NZB...");
            var step0Watch = Stopwatch.StartNew();

            List<NzbFile> nzbFiles;
            string nzbName;

            if (!string.IsNullOrEmpty(nzbFilePath))
            {
                if (!File.Exists(nzbFilePath))
                {
                    Console.WriteLine($"  ERROR: File not found: {nzbFilePath}");
                    return;
                }
                await using var fileStream = File.OpenRead(nzbFilePath);
                var nzbDoc = NzbDocument.Load(fileStream);
                nzbFiles = nzbDoc.Files.Where(x => x.Segments.Count > 0).ToList();
                nzbName = Path.GetFileName(nzbFilePath);
            }
            else if (!string.IsNullOrEmpty(searchPattern))
            {
                await using var db = new DavDatabaseContext();
                var historyItem = await db.HistoryItems
                    .Where(h => h.JobName.Contains(searchPattern) && h.NzbContents != null)
                    .OrderByDescending(h => h.CreatedAt)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);

                if (historyItem == null || historyItem.NzbContents == null)
                {
                    Console.WriteLine($"  ERROR: No history item found matching '{searchPattern}'");
                    Console.WriteLine("  Note: --test-import requires NZB contents stored in history.");
                    Console.WriteLine("  Use --file= to load NZB from disk instead.");
                    return;
                }

                var documentBytes = Encoding.UTF8.GetBytes(historyItem.NzbContents);
                using var stream = new MemoryStream(documentBytes);
                var nzbDoc = await NzbDocument.LoadAsync(stream).ConfigureAwait(false);
                nzbFiles = nzbDoc.Files.Where(x => x.Segments.Count > 0).ToList();
                nzbName = historyItem.JobName;
            }
            else
            {
                Console.WriteLine("  ERROR: No NZB file or search pattern provided");
                return;
            }

            step0Watch.Stop();
            timings["Step 0: Parse NZB"] = step0Watch.Elapsed.TotalSeconds;
            stepDetails["Step 0: Parse NZB"] = $"{nzbFiles.Count} files, {nzbFiles.Sum(f => f.Segments.Count)} segments";
            Console.WriteLine($"  ✓ Parsed {nzbFiles.Count} files with {nzbFiles.Sum(f => f.Segments.Count)} segments");
            Console.WriteLine($"  → {step0Watch.Elapsed.TotalSeconds:F2}s\n");

            // Create cancellation token with usage context
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var usageContext = new ConnectionUsageContext(ConnectionUsageType.Queue, nzbName);
            using var _ = cts.Token.SetScopedContext(usageContext);
            var ct = cts.Token;

            // Step 1a: Fetch first segments
            Console.WriteLine("Step 1a: Fetching first segments (16KB each)...");
            var step1aWatch = Stopwatch.StartNew();
            var lastProgress = 0;
            var progress = new Progress<int>(p =>
            {
                if (p - lastProgress >= 10 || p == nzbFiles.Count)
                {
                    Console.WriteLine($"  Progress: {p}/{nzbFiles.Count}");
                    lastProgress = p;
                }
            });

            var segments = await FetchFirstSegmentsStep.FetchFirstSegments(
                nzbFiles, client, configManager, ct, progress).ConfigureAwait(false);

            step1aWatch.Stop();
            timings["Step 1a: Fetch first segments"] = step1aWatch.Elapsed.TotalSeconds;
            var avgTimePerFile = step1aWatch.Elapsed.TotalSeconds / Math.Max(1, nzbFiles.Count);
            stepDetails["Step 1a: Fetch first segments"] = $"{segments.Count} segments, {avgTimePerFile:F3}s/file avg";
            Console.WriteLine($"  ✓ Fetched {segments.Count} first segments");
            Console.WriteLine($"  → {step1aWatch.Elapsed.TotalSeconds:F2}s ({avgTimePerFile:F3}s per file)\n");

            // Step 1b: Extract Par2 file descriptors
            Console.WriteLine("Step 1b: Extracting Par2 file descriptors...");
            var step1bWatch = Stopwatch.StartNew();

            var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
                segments, client, ct).ConfigureAwait(false);

            step1bWatch.Stop();
            timings["Step 1b: Par2 descriptors"] = step1bWatch.Elapsed.TotalSeconds;
            stepDetails["Step 1b: Par2 descriptors"] = $"{par2FileDescriptors.Count} descriptors";
            Console.WriteLine($"  ✓ Found {par2FileDescriptors.Count} Par2 file descriptors");
            Console.WriteLine($"  → {step1bWatch.Elapsed.TotalSeconds:F2}s\n");

            // Step 1c: Build file info objects
            Console.WriteLine("Step 1c: Building file info objects...");
            var step1cWatch = Stopwatch.StartNew();

            var fileInfos = GetFileInfosStep.GetFileInfos(segments, par2FileDescriptors);

            step1cWatch.Stop();
            timings["Step 1c: Build file infos"] = step1cWatch.Elapsed.TotalSeconds;
            var rarCount = fileInfos.Count(f => f.IsRar);
            var szCount = fileInfos.Count(f => f.IsSevenZip);
            stepDetails["Step 1c: Build file infos"] = $"{fileInfos.Count} infos, {rarCount} RAR, {szCount} 7z";
            Console.WriteLine($"  ✓ Built {fileInfos.Count} file infos ({rarCount} RAR, {szCount} 7z)");
            Console.WriteLine($"  → {step1cWatch.Elapsed.TotalSeconds:F2}s\n");

            // Step 1d: Fetch file sizes for files without Par2 descriptors
            var filesWithoutSize = fileInfos.Where(f => f.FileSize == null).Select(f => f.NzbFile).ToList();
            if (filesWithoutSize.Count > 0)
            {
                Console.WriteLine($"Step 1d: Fetching file sizes for {filesWithoutSize.Count} files without Par2...");
                var step1dWatch = Stopwatch.StartNew();

                var fileSizes = await client.GetFileSizesBatchAsync(filesWithoutSize, connections, ct).ConfigureAwait(false);

                foreach (var fileInfo in fileInfos.Where(f => f.FileSize == null))
                {
                    if (fileSizes.TryGetValue(fileInfo.NzbFile, out var size))
                    {
                        fileInfo.FileSize = size;
                    }
                }

                step1dWatch.Stop();
                timings["Step 1d: Fetch file sizes"] = step1dWatch.Elapsed.TotalSeconds;
                stepDetails["Step 1d: Fetch file sizes"] = $"{filesWithoutSize.Count} files";
                Console.WriteLine($"  ✓ Fetched {fileSizes.Count} file sizes");
                Console.WriteLine($"  → {step1dWatch.Elapsed.TotalSeconds:F2}s\n");
            }
            else
            {
                Console.WriteLine("Step 1d: Skipped (all files have Par2 sizes)\n");
                timings["Step 1d: Fetch file sizes"] = 0;
                stepDetails["Step 1d: Fetch file sizes"] = "skipped";
            }

            // Step 2: Process files (RAR headers, etc.)
            Console.WriteLine("Step 2: Processing files (RAR headers, etc.)...");
            var step2Watch = Stopwatch.StartNew();

            var maxConnections = configManager.GetMaxQueueConnections();
            var processors = new List<BaseProcessor>();

            // Group files by type
            var baseGroups = fileInfos
                .DistinctBy(x => x.FileName)
                .GroupBy(x => NzbWebDAV.Utils.FilenameUtil.GetMultipartBaseName(x.FileName))
                .ToList();

            var rarGroupCount = 0;
            var otherGroupCount = 0;

            foreach (var baseGroup in baseGroups)
            {
                var files = baseGroup.ToList();
                if (files.Any(x => x.IsRar || NzbWebDAV.Utils.FilenameUtil.IsRarFile(x.FileName)))
                {
                    rarGroupCount++;
                    var connectionsPerRar = Math.Max(1, Math.Min(5, maxConnections / Math.Max(1, rarGroupCount / 3)));
                    processors.Add(new RarProcessor(files, client, null, ct, connectionsPerRar));
                }
                else if (files.Any(x => x.IsSevenZip || NzbWebDAV.Utils.FilenameUtil.Is7zFile(x.FileName)))
                {
                    processors.Add(new SevenZipProcessor(files, client, null, ct));
                }
                else if (files.Any(x => NzbWebDAV.Utils.FilenameUtil.IsMultipartMkv(x.FileName)))
                {
                    processors.Add(new MultipartMkvProcessor(files, client, ct));
                }
                else
                {
                    otherGroupCount++;
                    foreach (var file in files)
                    {
                        processors.Add(new FileProcessor(file, client, ct));
                    }
                }
            }

            Console.WriteLine($"  Created {processors.Count} processors ({rarGroupCount} RAR groups, {otherGroupCount} other)");

            // Process with concurrency
            var fileConcurrency = Math.Max(1, Math.Min(connections, 145 / 5));
            var processedCount = 0;
            var results = new List<object?>();

            foreach (var processor in processors)
            {
                try
                {
                    var result = await processor.ProcessAsync().ConfigureAwait(false);
                    results.Add(result);
                    processedCount++;
                    if (processedCount % 5 == 0 || processedCount == processors.Count)
                    {
                        Console.WriteLine($"  Progress: {processedCount}/{processors.Count}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warning: Processor failed: {ex.Message}");
                }
            }

            step2Watch.Stop();
            timings["Step 2: File processing"] = step2Watch.Elapsed.TotalSeconds;
            var avgProcessTime = step2Watch.Elapsed.TotalSeconds / Math.Max(1, processors.Count);
            stepDetails["Step 2: File processing"] = $"{processors.Count} processors, {avgProcessTime:F3}s/proc avg";
            Console.WriteLine($"  ✓ Processed {processedCount}/{processors.Count} files");
            Console.WriteLine($"  → {step2Watch.Elapsed.TotalSeconds:F2}s ({avgProcessTime:F3}s per processor)\n");

            overallWatch.Stop();

            // Print summary
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("  TIMING SUMMARY");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine($"  NZB: {nzbName}");
            Console.WriteLine($"  Files: {nzbFiles.Count}, Segments: {nzbFiles.Sum(f => f.Segments.Count)}");
            Console.WriteLine($"  Connections: {connections}");
            Console.WriteLine("───────────────────────────────────────────────────────────────");

            var totalTimed = timings.Values.Sum();
            foreach (var (step, elapsed) in timings.OrderBy(x => x.Key))
            {
                var pct = 100.0 * elapsed / totalTimed;
                var detail = stepDetails.GetValueOrDefault(step, "");
                var bar = new string('█', (int)(pct / 5));
                Console.WriteLine($"  {step,-30} {elapsed,7:F2}s ({pct,5:F1}%) {bar}");
                if (!string.IsNullOrEmpty(detail))
                {
                    Console.WriteLine($"    └─ {detail}");
                }
            }

            Console.WriteLine("───────────────────────────────────────────────────────────────");
            Console.WriteLine($"  {"TOTAL",-30} {overallWatch.Elapsed.TotalSeconds,7:F2}s");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");

            // Bottleneck analysis
            Console.WriteLine("\n  BOTTLENECK ANALYSIS:");
            var sorted = timings.OrderByDescending(x => x.Value).ToList();
            var biggest = sorted.First();
            Console.WriteLine($"  → Slowest step: {biggest.Key} ({biggest.Value:F2}s, {100.0 * biggest.Value / totalTimed:F1}%)");

            if (biggest.Key.Contains("1a"))
            {
                Console.WriteLine("    Recommendation: Increase connections or check provider latency");
            }
            else if (biggest.Key.Contains("1b"))
            {
                Console.WriteLine("    Recommendation: Par2 extraction is slow - check provider speed");
            }
            else if (biggest.Key.Contains("2"))
            {
                Console.WriteLine("    Recommendation: RAR header parsing is slow - check archive complexity");
            }

            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            overallWatch.Stop();
            Console.WriteLine($"\nERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
