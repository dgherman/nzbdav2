using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
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
                        Console.WriteLine($"  RAR Processing Success. Extracted segments: {rarResult.StoredFileSegments.Length}");
                        var filesInArchive = rarResult.StoredFileSegments.GroupBy(s => s.PathWithinArchive);
                        foreach (var archivedFile in filesInArchive)
                        {
                            var totalSize = archivedFile.Sum(s => s.ByteRangeWithinPart.Count);
                            Console.WriteLine($"    - {archivedFile.Key}: {totalSize} bytes in {archivedFile.Count()} segments");
                            foreach (var seg in archivedFile.OrderBy(s => s.PartNumber))
                            {
                                Console.WriteLine($"      Part {seg.PartNumber}: Offset={seg.ByteRangeWithinPart.StartInclusive}, Count={seg.ByteRangeWithinPart.Count}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("  RAR Processing Failed (Result was null)");
                    }
                }
            }

        } catch (Exception ex) {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}
