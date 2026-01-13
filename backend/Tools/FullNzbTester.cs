using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using NzbWebDAV.Streams;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;
using NzbWebDAV.Models;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;
using Usenet.Nzb;

namespace NzbWebDAV.Tools;

public class FullNzbTester
{
    public static async Task RunAsync(string[] args)
    {
        var argIndex = args.ToList().IndexOf("--test-full-nzb");
        var useMockServer = args.Contains("--mock-server");

        if (args.Length <= argIndex + 1 && !useMockServer)
        {
            Console.WriteLine("Usage: --test-full-nzb <nzbPath>");
            Console.WriteLine("       --test-full-nzb --mock-server [--latency=MS] [--jitter=MS] [--size=MB]");
            Console.WriteLine("");
            Console.WriteLine("Options:");
            Console.WriteLine("  --mock-server    Start built-in mock NNTP server and generate mock.nzb");
            Console.WriteLine("  --latency=MS     Base latency in ms (default: 50)");
            Console.WriteLine("  --jitter=MS      Latency jitter in ms (default: 10)");
            Console.WriteLine("  --size=MB        Size of mock file in MB (default: 200)");
            return;
        }

        var services = new ServiceCollection();
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);

        // Check for --mock-server option (built-in mock server)
        MockNntpServer? mockServer = null;
        string? nzbPath = null;

        if (useMockServer)
        {
            var port = 1190;
            var latencyMs = 50;
            var jitterMs = 10;
            var segmentSize = 700 * 1024;
            var totalSizeMb = 200;

            // Parse optional args
            var connectionsPerStream = 20; // Higher default for benchmark
            foreach (var arg in args)
            {
                if (arg.StartsWith("--latency=")) int.TryParse(arg.Substring(10), out latencyMs);
                if (arg.StartsWith("--jitter=")) int.TryParse(arg.Substring(9), out jitterMs);
                if (arg.StartsWith("--size=")) int.TryParse(arg.Substring(7), out totalSizeMb);
                if (arg.StartsWith("--connections=")) int.TryParse(arg.Substring(14), out connectionsPerStream);
            }

            Console.WriteLine($"═══════════════════════════════════════════════════════════════");
            Console.WriteLine($"  MOCK SERVER BENCHMARK");
            Console.WriteLine($"  Latency: {latencyMs}ms, Jitter: {jitterMs}ms, File Size: {totalSizeMb}MB");
            Console.WriteLine($"  Connections per stream: {connectionsPerStream}");
            Console.WriteLine($"═══════════════════════════════════════════════════════════════");

            // Disable smart analysis for mock server (segments are synthetic)
            Environment.SetEnvironmentVariable("BENCHMARK", "true");

            // Enable detailed timing for performance analysis
            BufferedSegmentStream.EnableDetailedTiming = true;
            BufferedSegmentStream.ResetGlobalTimingStats();

            // Start mock server
            Console.WriteLine($"Starting mock NNTP server on port {port}...");
            mockServer = new MockNntpServer(port, latencyMs, segmentSize, jitterMs, 0.0);
            mockServer.Start();

            // Generate mock NZB
            nzbPath = "mock.nzb";
            var totalSize = (long)totalSizeMb * 1024 * 1024;
            Console.WriteLine($"Generating {nzbPath} ({totalSizeMb}MB)...");
            await MockNzbGenerator.GenerateAsync(nzbPath, totalSize, segmentSize);

            var mockConfig = new UsenetProviderConfig
            {
                Providers = new List<UsenetProviderConfig.ConnectionDetails>
                {
                    new()
                    {
                        Type = ProviderType.Pooled,
                        Host = "127.0.0.1",
                        Port = port,
                        UseSsl = false,
                        User = "mock",
                        Pass = "mock",
                        MaxConnections = connectionsPerStream
                    }
                }
            };

            // Set config values for mock testing
            // CRITICAL: Set queue/repair connections to 1 so GlobalOperationLimiter allows streaming
            configManager.UpdateValues(new List<ConfigItem>
            {
                new() { ConfigName = "usenet.providers", ConfigValue = JsonSerializer.Serialize(mockConfig) },
                new() { ConfigName = "usenet.connections-per-stream", ConfigValue = connectionsPerStream.ToString() },
                new() { ConfigName = "api.max-queue-connections", ConfigValue = "1" },
                new() { ConfigName = "repair.connections", ConfigValue = "1" }
            });
        }
        else
        {
            // Find NZB path (last non-option argument after --test-full-nzb)
            for (int i = argIndex + 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--"))
                {
                    nzbPath = args[i];
                    break;
                }
            }

            if (string.IsNullOrEmpty(nzbPath))
            {
                Console.WriteLine("Error: No NZB path provided.");
                return;
            }
        }

        Console.WriteLine($"Processing NZB: {nzbPath}");

        services.AddSingleton(configManager);
        services.AddSingleton<WebsocketManager>();
        services.AddSingleton<BandwidthService>();
        services.AddSingleton<ProviderErrorService>();
        services.AddSingleton<NzbProviderAffinityService>();
        services.AddSingleton<UsenetStreamingClient>();
        services.AddDbContext<DavDatabaseContext>();

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<UsenetStreamingClient>();

        try {
            // Load NZB
            using var fs = File.OpenRead(nzbPath);
            var nzb = await NzbDocument.LoadAsync(fs).ConfigureAwait(false);
            var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();
            Console.WriteLine($"Loaded NZB. Files: {nzbFiles.Count}");

            // Step 1: Fetch first segments
            Console.WriteLine("\n--- STEP 1: FETCH FIRST SEGMENTS ---");
            var segments = await FetchFirstSegmentsStep.FetchFirstSegments(
                nzbFiles, client, configManager, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Fetched {segments.Count} first segments.");

            // Step 2: Get Par2 descriptors
            Console.WriteLine("\n--- STEP 2: GET PAR2 DESCRIPTORS ---");
            var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
                segments, client, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Found {par2FileDescriptors.Count} Par2 descriptors.");

            // Step 3: Get File Infos
            Console.WriteLine("\n--- STEP 3: GET FILE INFOS ---");
            var fileInfos = GetFileInfosStep.GetFileInfos(segments, par2FileDescriptors);
            foreach (var fi in fileInfos) {
                Console.WriteLine($"File: {fi.FileName}, IsRar: {fi.IsRar}, MagicOffset: {fi.MagicOffset}");
            }

            // Step 4: Smart Grouping (Manual Replication of QueueItemProcessor logic)
            Console.WriteLine("\n--- STEP 4: SMART GROUPING ---");
            var baseGroups = fileInfos
                .DistinctBy(x => x.FileName)
                .GroupBy(x => FilenameUtil.GetMultipartBaseName(x.FileName))
                .ToList();

            var allResults = new System.Collections.Generic.List<RarProcessor.Result>();

            foreach (var baseGroup in baseGroups)
            {
                var files = baseGroup.ToList();
                var groupType = "other";

                if (files.Any(x => x.IsRar || FilenameUtil.IsRarFile(x.FileName))) groupType = "rar";
                else if (files.Any(x => x.IsSevenZip || FilenameUtil.Is7zFile(x.FileName))) groupType = "7z";
                else if (files.Any(x => FilenameUtil.IsMultipartMkv(x.FileName))) groupType = "multipart-mkv";

                Console.WriteLine($"Group: {baseGroup.Key}, Type: {groupType}, Files: {files.Count}");

                if (groupType == "rar")
                {
                    var processor = new RarProcessor(files, client, null, CancellationToken.None, 1);
                    var result = await processor.ProcessAsync().ConfigureAwait(false);
                    
                    if (result is RarProcessor.Result rarResult)
                    {
                        allResults.Add(rarResult);
                        Console.WriteLine($"  RAR Processing Success. Extracted segments: {rarResult.StoredFileSegments.Length}");
                        var filesInArchive = rarResult.StoredFileSegments.GroupBy(s => s.PathWithinArchive);
                        foreach (var archivedFile in filesInArchive)
                        {
                            var totalSize = archivedFile.Sum(s => s.ByteRangeWithinPart.Count);
                            Console.WriteLine($"    - {archivedFile.Key}: {totalSize} bytes in {archivedFile.Count()} segments");
                            foreach (var seg in archivedFile.OrderBy(s => s.PartNumber))
                            {
                                Console.WriteLine($"      Part {seg.PartNumber}: Offset={seg.ByteRangeWithinPart.StartInclusive}, Count={seg.ByteRangeWithinPart.Count}, PartSize={seg.PartSize}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("  RAR Processing Failed (Result was null)");
                    }
                }
                else if (groupType == "other")
                {
                    // Handle plain non-RAR files (like test-2.nzb)
                    Console.WriteLine($"  Non-RAR file detected. Treating as single-part file for throughput testing.");
                    var file = files.First();

                    // Calculate file size from segment sizes if available, otherwise use approximation
                    long fileSize;
                    if (file.SegmentSizes != null && file.SegmentSizes.Length > 0)
                    {
                        fileSize = file.SegmentSizes.Sum();
                    }
                    else if (file.FileSize.HasValue && file.FileSize.Value > 0)
                    {
                        fileSize = file.FileSize.Value;
                    }
                    else
                    {
                        // Fallback: approximate from article count (rough estimate)
                        var segmentCount = file.NzbFile.GetSegmentIds().Length;
                        fileSize = segmentCount * 700000L; // ~700KB per segment average
                    }

                    Console.WriteLine($"    File size: {fileSize / 1024.0 / 1024.0:F2} MB ({file.NzbFile.GetSegmentIds().Length} segments)");

                    // Create a simple single-part file structure
                    allResults.Add(new RarProcessor.Result
                    {
                        StoredFileSegments = new[]
                        {
                            new RarProcessor.StoredFileSegment
                            {
                                NzbFile = file.NzbFile,
                                PartNumber = 1,
                                PartSize = fileSize,
                                ArchiveName = file.FileName,
                                PathWithinArchive = file.FileName,
                                ByteRangeWithinPart = LongRange.FromStartAndSize(0, fileSize),
                                AesParams = null,
                                ReleaseDate = file.ReleaseDate
                            }
                        }
                    });
                }
            }

            // Select the largest result for analysis
            var finalRarResult = allResults
                .SelectMany(r => r.StoredFileSegments.GroupBy(s => s.PathWithinArchive))
                .MaxBy(g => g.Sum(s => s.ByteRangeWithinPart.Count))
                ?.First().NzbFile != null 
                    ? allResults.FirstOrDefault(r => r.StoredFileSegments.Any(s => s.PathWithinArchive == allResults.SelectMany(x => x.StoredFileSegments).MaxBy(y => y.ByteRangeWithinPart.Count)?.PathWithinArchive))
                    : null;
            
            // Simplified selection: just pick the result containing the largest file
            var largestFileSegment = allResults
                .SelectMany(r => r.StoredFileSegments)
                .MaxBy(s => s.ByteRangeWithinPart.Count);
                
            if (largestFileSegment != null)
            {
                finalRarResult = allResults.First(r => r.StoredFileSegments.Contains(largestFileSegment));
            }

            // Step 5: FFprobe Analysis
            if (finalRarResult != null)
            {
                Console.WriteLine("\n--- STEP 5: FFPROBE ANALYSIS ---");
                
                // Find largest file (likely video)
                var filesInArchive = finalRarResult.StoredFileSegments.GroupBy(s => s.PathWithinArchive);
                var videoFile = filesInArchive.MaxBy(g => g.Sum(s => s.ByteRangeWithinPart.Count));
                
                if (videoFile != null)
                {
                    Console.WriteLine($"Analyzing largest file: {videoFile.Key}");
                    
                    var aesParams = videoFile.Select(x => x.AesParams).FirstOrDefault(x => x != null);
                    Console.WriteLine($"Encryption: {(aesParams != null ? "Detected" : "None")}");

                    // Build parts
                    var fileParts = videoFile
                        .OrderBy(s => s.PartNumber)
                        .Select(x => new DavMultipartFile.FilePart
                        {
                            SegmentIds = x.NzbFile.GetSegmentIds(),
                            SegmentIdByteRange = LongRange.FromStartAndSize(0, x.PartSize),
                            FilePartByteRange = x.ByteRangeWithinPart
                        })
                        .ToArray();
                        
                    Stream stream = new DavMultipartFileStream(
                        fileParts,
                        client,
                        configManager.GetConnectionsPerStream(),
                        new ConnectionUsageContext(ConnectionUsageType.Streaming)
                    );
                    
                    if (aesParams != null)
                    {
                        stream = new AesDecoderStream(stream, aesParams);
                    }
                    
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "ffprobe",
                            Arguments = "-hide_banner -show_streams -",
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        };
                        
                        using var process = Process.Start(psi);
                        if (process == null) throw new Exception("Failed to start ffprobe");
                        
                        var stdoutTask = process.StandardOutput.ReadToEndAsync();
                        var errorTask = process.StandardError.ReadToEndAsync();
                        
                        try
                        {
                            await stream.CopyToAsync(process.StandardInput.BaseStream).ConfigureAwait(false);
                            process.StandardInput.Close();
                        }
                        catch (IOException) 
                        {
                            // Ignore broken pipe (ffprobe exited early)
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error writing to ffprobe: {ex.Message}");
                        }
                        
                        await process.WaitForExitAsync().ConfigureAwait(false);
                        var stdout = await stdoutTask.ConfigureAwait(false);
                        var stderr = await errorTask.ConfigureAwait(false);
                        
                        if (process.ExitCode == 0)
                        {
                            Console.WriteLine("FFprobe Success! File stream is valid.");
                            Console.WriteLine("Stream Details:");
                            Console.WriteLine(stdout);
                        }
                        else
                        {
                            Console.WriteLine($"FFprobe Failed (Exit Code {process.ExitCode}):");
                            Console.WriteLine(stderr);
                        }

                        Console.WriteLine();
                        Console.WriteLine("--- STEP 6: SCRUBBING/SEEKING SIMULATION ---");

                        // Create a fresh stream for seeking tests (previous stream was consumed by ffprobe)
                        Stream scrubStream = new DavMultipartFileStream(
                            fileParts,
                            client,
                            configManager.GetConnectionsPerStream(),
                            new ConnectionUsageContext(ConnectionUsageType.Streaming)
                        );
                        if (aesParams != null)
                        {
                            scrubStream = new AesDecoderStream(scrubStream, aesParams);
                        }

                        var percentages = new[] { 0.1, 0.5, 0.9, 0.2 };
                        var buffer = new byte[1024];
                        var seekTimes = new System.Collections.Generic.List<(double Pct, long Ms)>();
                        var totalScrubWatch = Stopwatch.StartNew();

                        foreach (var pct in percentages)
                        {
                            try
                            {
                                var targetPos = (long)(scrubStream.Length * pct);
                                Console.WriteLine($"Seeking to {pct:P0} ({targetPos} bytes)...");

                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                var seekWatch = Stopwatch.StartNew();
                                scrubStream.Seek(targetPos, SeekOrigin.Begin);
                                seekWatch.Stop();

                                var readWatch = Stopwatch.StartNew();
                                var read = await scrubStream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                                readWatch.Stop();

                                // We count "Seek Time" as the full latency users perceive (Seek + first Read)
                                var totalLatency = seekWatch.ElapsedMilliseconds + readWatch.ElapsedMilliseconds;
                                seekTimes.Add((pct, totalLatency));

                                Console.WriteLine($"  - Seek Time: {seekWatch.ElapsedMilliseconds}ms");
                                Console.WriteLine($"  - Read Time: {readWatch.ElapsedMilliseconds}ms");
                                Console.WriteLine($"  - Read Bytes: {read}");

                                if (read == 0) Console.WriteLine("  WARNING: Read 0 bytes!");
                            }
                            catch (OperationCanceledException)
                            {
                                Console.WriteLine("  TIMEOUT: Operation took longer than 30s");
                                seekTimes.Add((pct, -1)); // -1 indicates timeout
                            }
                        }

                        totalScrubWatch.Stop();
                        await scrubStream.DisposeAsync();
                        Console.WriteLine($"Total Scrubbing Time: {totalScrubWatch.Elapsed.TotalSeconds:F2}s");

                        // Sequential Throughput Benchmark
                        Console.WriteLine();
                        Console.WriteLine("--- STEP 7: SEQUENTIAL THROUGHPUT BENCHMARK ---");
                        double sequentialSpeed = 0;

                        // Reset timing stats for clean throughput measurement
                        BufferedSegmentStream.ResetGlobalTimingStats();

                        try
                        {
                            var streamCreateWatch = Stopwatch.StartNew();
                            using var throughputStream = new DavMultipartFileStream(
                                fileParts,
                                client,
                                configManager.GetConnectionsPerStream(),
                                new ConnectionUsageContext(ConnectionUsageType.Streaming)
                            );
                            streamCreateWatch.Stop();
                            Console.WriteLine($"Stream creation time: {streamCreateWatch.ElapsedMilliseconds}ms");

                            Stream benchStream = throughputStream;
                            if (aesParams != null) benchStream = new AesDecoderStream(throughputStream, aesParams);

                            // Benchmark up to 200MB or the full file length if it's close to that
                            long targetBytes = Math.Min(benchStream.Length, 200 * 1024 * 1024);
                            var benchBuffer = new byte[256 * 1024]; // 256 KB buffer
                            long totalBenchRead = 0;
                            int readCount = 0;
                            double totalReadTime = 0;
                            double minReadTime = double.MaxValue;
                            double maxReadTime = 0;
                            var readTimes = new List<double>();

                            Console.WriteLine($"Benchmarking sequential read of {targetBytes / 1024 / 1024} MB...");
                            var benchWatch = Stopwatch.StartNew();
                            using var benchCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

                            while (totalBenchRead < targetBytes)
                            {
                                var readWatch = Stopwatch.StartNew();
                                int read = await benchStream.ReadAsync(benchBuffer, 0, benchBuffer.Length, benchCts.Token).ConfigureAwait(false);
                                readWatch.Stop();

                                if (read == 0) break;

                                var readTimeMs = readWatch.Elapsed.TotalMilliseconds;
                                totalReadTime += readTimeMs;
                                readTimes.Add(readTimeMs);
                                minReadTime = Math.Min(minReadTime, readTimeMs);
                                maxReadTime = Math.Max(maxReadTime, readTimeMs);

                                totalBenchRead += read;
                                readCount++;

                                // Report progress every 10MB
                                if (totalBenchRead % (10 * 1024 * 1024) < benchBuffer.Length)
                                {
                                    var currentSpeed = (totalBenchRead / 1024.0 / 1024.0) / benchWatch.Elapsed.TotalSeconds;
                                    Console.WriteLine($"  Progress: {totalBenchRead / 1024.0 / 1024.0:F1} MB @ {currentSpeed:F2} MB/s (last read: {read / 1024.0:F1} KB in {readTimeMs:F1}ms)");
                                }
                            }

                            benchWatch.Stop();
                            sequentialSpeed = (totalBenchRead / 1024.0 / 1024.0) / benchWatch.Elapsed.TotalSeconds;

                            // Calculate statistics
                            var avgReadTime = totalReadTime / readCount;
                            readTimes.Sort();
                            var medianReadTime = readTimes[readTimes.Count / 2];
                            var p95ReadTime = readTimes[(int)(readTimes.Count * 0.95)];

                            Console.WriteLine($"\nRead {totalBenchRead / 1024.0 / 1024.0:F2} MB in {benchWatch.Elapsed.TotalSeconds:F2}s");
                            Console.WriteLine($"Sequential Speed: {sequentialSpeed:F2} MB/s");
                            Console.WriteLine($"\nRead Statistics:");
                            Console.WriteLine($"  Total Reads: {readCount}");
                            Console.WriteLine($"  Avg Read Time: {avgReadTime:F2}ms");
                            Console.WriteLine($"  Median Read Time: {medianReadTime:F2}ms");
                            Console.WriteLine($"  Min/Max Read Time: {minReadTime:F2}ms / {maxReadTime:F2}ms");
                            Console.WriteLine($"  P95 Read Time: {p95ReadTime:F2}ms");
                            Console.WriteLine($"  Time in ReadAsync: {totalReadTime:F2}ms ({totalReadTime / benchWatch.Elapsed.TotalMilliseconds * 100:F1}% of total)");

                            // Print detailed timing breakdown
                            if (BufferedSegmentStream.EnableDetailedTiming)
                            {
                                BufferedSegmentStream.GetGlobalTimingStats().Print();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Benchmark failed: {ex.Message}");
                        }

                        // Summary Table
                        Console.WriteLine();
                        Console.WriteLine("═══════════════════════════════════════════════════════════════");
                        Console.WriteLine("  FULL NZB TESTER RESULTS SUMMARY");
                        Console.WriteLine("═══════════════════════════════════════════════════════════════");
                        Console.WriteLine($"  File Processed:       {Path.GetFileName(nzbPath)}");
                        Console.WriteLine($"  Total Files:          {nzbFiles.Count}");
                        Console.WriteLine("───────────────────────────────────────────────────────────────");
                        Console.WriteLine("  SCRUBBING LATENCY (Seek + First Read):");
                        foreach (var (pct, ms) in seekTimes)
                        {
                            var color = ms < 1000 ? "" : (ms < 5000 ? "(!)" : "(!!)");
                            Console.WriteLine($"    Seek to {pct,3:P0}:         {ms,6} ms {color}"); 
                        }
                        Console.WriteLine($"    Total Scrub Time:   {totalScrubWatch.Elapsed.TotalSeconds,6:F2} s");
                        Console.WriteLine("───────────────────────────────────────────────────────────────");
                        Console.WriteLine("  SEQUENTIAL THROUGHPUT:");
                        Console.WriteLine($"    Speed:              {sequentialSpeed,6:F2} MB/s");
                        Console.WriteLine("═══════════════════════════════════════════════════════════════");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Analysis failed: {ex.Message}");
                    }

                    // Seek Test
                    Console.WriteLine("\n--- SEEK TEST ---");
                    try 
                    {
                        using var seekStream = new DavMultipartFileStream(
                            fileParts,
                            client,
                            configManager.GetConnectionsPerStream(),
                            new ConnectionUsageContext(ConnectionUsageType.Streaming)
                        );
                        
                        Stream testStream = seekStream;
                        if (aesParams != null) testStream = new AesDecoderStream(seekStream, aesParams);

                        var length = testStream.Length;
                        var seekOffset = Math.Max(0, length - 1024);
                        Console.WriteLine($"Seeking to offset {seekOffset} (Length: {length})");
                        
                        testStream.Seek(seekOffset, SeekOrigin.Begin);
                        
                        var buffer = new byte[1024];
                        int read = await testStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        
                        Console.WriteLine($"Seek Read Success. Read {read} bytes.");
                        if (read > 0)
                        {
                            var preview = BitConverter.ToString(buffer.Take(16).ToArray()).Replace("-", " ");
                            Console.WriteLine($"Last bytes preview: {preview} ...");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Seek Test Failed: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                    }
                }
            }

        } catch (Exception ex) {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // Cleanup mock server if started
            mockServer?.Dispose();
        }
    }
}
