using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        if (args.Length <= argIndex + 1)
        {
            Console.WriteLine("Usage: --test-full-nzb <nzbPath>");
            return;
        }
        
        string nzbPath = args[argIndex + 1];
        Console.WriteLine($"Processing NZB: {nzbPath}");
        
        var services = new ServiceCollection();
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);
        
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

            RarProcessor.Result? finalRarResult = null;

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
                        finalRarResult = rarResult;
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
                            Arguments = "-v error -",
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        };
                        
                        using var process = Process.Start(psi);
                        if (process == null) throw new Exception("Failed to start ffprobe");
                        
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
                        var stderr = await errorTask.ConfigureAwait(false);
                        
                        if (process.ExitCode == 0)
                        {
                            Console.WriteLine("FFprobe Success! File stream is valid.");
                        }
                        else
                        {
                            Console.WriteLine($"FFprobe Failed (Exit Code {process.ExitCode}):");
                            Console.WriteLine(stderr);
                        }
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
    }
}
