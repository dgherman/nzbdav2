using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tools;

public class MagicTester
{
    public static async Task RunAsync(string[] args)
    {
        var argIndex = args.ToList().IndexOf("--magic-test");
        if (args.Length <= argIndex + 1)
        {
            Console.WriteLine("Usage: --magic-test <segmentId> OR --magic-test --find <filename>");
            return;
        }

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

        string segmentId;
        if (args[argIndex + 1] == "--generate-nzb")
        {
            if (args.Length <= argIndex + 2)
            {
                Console.WriteLine("Usage: --magic-test --generate-nzb <filename>");
                return;
            }
            string filename = args[argIndex + 2];
            Console.WriteLine($"Looking up file to generate NZB: {filename}");
            
            using var db = sp.GetRequiredService<DavDatabaseContext>();
            var item = await db.Items.FirstOrDefaultAsync(i => i.Name == filename).ConfigureAwait(false);
            if (item == null)
            {
                item = await db.Items.FirstOrDefaultAsync(i => i.Name.Contains(filename)).ConfigureAwait(false);
                if (item == null)
                {
                    Console.WriteLine("File not found in DB.");
                    return;
                }
            }
            Console.WriteLine($"Found item: {item.Name} ({item.Id})");
            
            var multiFile = await db.MultipartFiles.FindAsync(item.Id);
            if (multiFile != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
                sb.AppendLine("<!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\">");
                sb.AppendLine("<nzb>");
                sb.AppendLine("  <head>");
                sb.AppendLine("    <meta type=\"category\">Video</meta>");
                sb.AppendLine("  </head>");
                
                int fileIndex = 1;
                foreach (var part in multiFile.Metadata.FileParts)
                {
                    // Escape special characters in subject
                    var subject = $"{item.Name} - Part {fileIndex}".Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
                    
                    sb.AppendLine($"  <file poster=\"yEncBin@test.com (yEncBin)\" date=\"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\" subject=\"{subject}\">");
                    sb.AppendLine("    <groups><group>alt.binaries.test</group></groups>");
                    sb.AppendLine("    <segments>");
                    for (int i = 0; i < part.SegmentIds.Length; i++)
                    {
                        var segId = part.SegmentIds[i].Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                        sb.AppendLine($"      <segment bytes=\"700000\" number=\"{i + 1}\">{segId}</segment>");
                    }
                    sb.AppendLine("    </segments>");
                    sb.AppendLine("  </file>");
                    fileIndex++;
                }
                sb.AppendLine("</nzb>");
                
                await File.WriteAllTextAsync("generated.nzb", sb.ToString());
                Console.WriteLine("NZB Generated to 'generated.nzb'");
                return;
            }
            else
            {
                Console.WriteLine("Not a DavMultipartFile.");
                return;
            }
        }
        else if (args[argIndex + 1] == "--inspect-nzb")
        {
            if (args.Length <= argIndex + 2)
            {
                Console.WriteLine("Usage: --magic-test --inspect-nzb <path_to_nzb>");
                return;
            }
            string path = args[argIndex + 2];
            Console.WriteLine($"Inspecting NZB: {path}");
            
            using var fileStream = File.OpenRead(path);
            var nzb = await Usenet.Nzb.NzbDocument.LoadAsync(fileStream);
            
            Console.WriteLine($"NZB Loaded. Files: {nzb.Files.Count}");
            foreach (var f in nzb.Files)
            {
                Console.WriteLine($"File: {f.Subject} ({f.Segments.Count} segments)");
                var firstSeg = f.Segments.OrderBy(s => s.Number).FirstOrDefault();
                if (firstSeg != null)
                {
                    Console.WriteLine($"  First Segment ({firstSeg.Number}): {firstSeg.MessageId}");
                    segmentId = firstSeg.MessageId;
                    
                    // Verify this segment
                    Console.WriteLine($"  -> Verifying Segment {segmentId}...");
                    try {
                        var rawStream = await client.GetSegmentStreamAsync(segmentId, false, CancellationToken.None).ConfigureAwait(false);
                        byte[] rawBuffer = new byte[128];
                        int rawRead = await rawStream.ReadAsync(rawBuffer, 0, rawBuffer.Length).ConfigureAwait(false);
                        Console.WriteLine($"     Read {rawRead} bytes.");
                        PrintHex(rawBuffer, 64);
                    } catch (Exception ex) {
                        Console.WriteLine($"     Verify Failed: {ex.Message}");
                    }
                }
            }
            return;
        }
        else if (args[argIndex + 1] == "--inspect-local-rar")
        {
            if (args.Length <= argIndex + 2)
            {
                Console.WriteLine("Usage: --magic-test --inspect-local-rar <path_to_rar>");
                return;
            }
            string path = args[argIndex + 2];
            Console.WriteLine($"Inspecting Local RAR: {path}");
            
            using var fileStream = File.OpenRead(path);
            
            Console.WriteLine("--- Header Inspection ---");
            var readerOptions = new SharpCompress.Readers.ReaderOptions();
            var headerFactory = new SharpCompress.Common.Rar.Headers.RarHeaderFactory(SharpCompress.IO.StreamingMode.Seekable, readerOptions);
            
            foreach (var header in headerFactory.ReadHeaders(fileStream))
            {
                if (header.HeaderType == SharpCompress.Common.Rar.Headers.HeaderType.File)
                {
                    Console.WriteLine($"File Header: {header.GetFileName()}");
                    Console.WriteLine($"  IsEncrypted: {header.GetIsEncrypted()}");
                    Console.WriteLine($"  IsSolid: {header.GetIsSolid()}");
                    Console.WriteLine($"  CompressionMethod: {header.GetCompressionMethod()}");
                    Console.WriteLine($"  DataStartPosition: {header.GetDataStartPosition()}");
                    Console.WriteLine($"  AdditionalDataSize: {header.GetAdditionalDataSize()}");
                    
                    if (header.GetIsEncrypted())
                    {
                        var aes = header.GetAesParams(null);
                        Console.WriteLine($"  AesParams (No Pass): {(aes == null ? "Null" : "Present")}");
                    }
                    
                    Console.WriteLine("\n--- Extraction Test ---");
                    try
                    {
                        using var archive = SharpCompress.Archives.Rar.RarArchive.Open(path);
                        foreach (var entry in archive.Entries)
                        {
                            if (!entry.IsDirectory)
                            {
                                Console.WriteLine($"Extracting entry: {entry.Key}");
                                using var entryStream = entry.OpenEntryStream();
                                var extractedBuffer = new byte[128];
                                var extractedRead = entryStream.Read(extractedBuffer, 0, extractedBuffer.Length);
                                Console.WriteLine($"Read {extractedRead} bytes.");
                                PrintHex(extractedBuffer, extractedRead);
                                
                                if (extractedRead >= 4 && extractedBuffer[0] == 0x1A && extractedBuffer[1] == 0x45 && extractedBuffer[2] == 0xDF && extractedBuffer[3] == 0xA3)
                                {
                                    Console.WriteLine("-> SUCCESS: Found EBML Header (MKV)!");
                                }
                                else
                                {
                                    Console.WriteLine("-> FAIL: No MKV header found.");
                                }
                                return; // Just check the first file
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Extraction Failed: {ex.Message}");
                    }
                    return;
                }
            }
            return;
        }
        else if (args[argIndex + 1] == "--find")
        {
            if (args.Length <= argIndex + 2)
            {
                Console.WriteLine("Usage: --magic-test --find <filename>");
                return;
            }
            string filename = args[argIndex + 2];
            Console.WriteLine($"Looking up file: {filename}");
            
            using var db = sp.GetRequiredService<DavDatabaseContext>();
            var item = await db.Items.FirstOrDefaultAsync(i => i.Name == filename).ConfigureAwait(false);
            if (item == null)
            {
                // try partial match
                item = await db.Items.FirstOrDefaultAsync(i => i.Name.Contains(filename)).ConfigureAwait(false);
                if (item == null)
                {
                    Console.WriteLine("File not found in DB.");
                    return;
                }
            }
            Console.WriteLine($"Found item: {item.Name} ({item.Id})");
            
            // Check NzbFile
            var nzbFile = await db.NzbFiles.FindAsync(item.Id);
            if (nzbFile != null)
            {
                segmentId = nzbFile.SegmentIds.FirstOrDefault() ?? "MISSING";
                Console.WriteLine($"Type: DavNzbFile, First Segment: {segmentId}");
            }
            else
            {
                // Check RarFile
                var rarFile = await db.RarFiles.FindAsync(item.Id);
                if (rarFile != null)
                {
                    var firstPart = rarFile.RarParts.FirstOrDefault();
                    if (firstPart != null)
                    {
                        segmentId = firstPart.SegmentIds.FirstOrDefault() ?? "MISSING";
                        Console.WriteLine($"Type: DavRarFile, First Segment: {segmentId}");
                    }
                    else
                    {
                        Console.WriteLine("DavRarFile found but no parts?");
                        return;
                    }
                }
                else
                {
                    // Check MultipartFile
                    var multiFile = await db.MultipartFiles.FindAsync(item.Id);
                    if (multiFile != null)
                    {
                         var firstPart = multiFile.Metadata.FileParts.FirstOrDefault();
                         if (firstPart != null)
                         {
                             segmentId = firstPart.SegmentIds.FirstOrDefault() ?? "MISSING";
                             Console.WriteLine($"Type: DavMultipartFile, First Segment: {segmentId}");
                             Console.WriteLine("--- File Parts ---");
                             foreach (var part in multiFile.Metadata.FileParts)
                             {
                                 Console.WriteLine($"Part: Segs={part.SegmentIds.Length}, SegRange={part.SegmentIdByteRange}, PartRange={part.FilePartByteRange}");
                             }
                             Console.WriteLine("------------------");
                             
                             if (multiFile.Metadata.AesParams != null)
                             {
                                 Console.WriteLine("-> Encrypted with AES.");
                             }

                             // Test the reconstructed stream
                             Console.WriteLine("\n--- TEST 3: DavMultipartFileStream (Reconstructed) ---");
                             try
                             {
                                 var stream = new DavMultipartFileStream(
                                     multiFile.Metadata.FileParts,
                                     client,
                                     1
                                 );
                                 
                                 Stream finalStream = stream;
                                 if (multiFile.Metadata.AesParams != null)
                                 {
                                     finalStream = new AesDecoderStream(stream, multiFile.Metadata.AesParams);
                                 }

                                 var buffer = new byte[1024];
                                 var read = await finalStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                                 Console.WriteLine($"Read {read} bytes from reconstructed stream.");
                                 PrintHex(buffer, read);
                                 
                                 var mkvSig = FindMagic(buffer, read, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
                                 if (mkvSig >= 0)
                                 {
                                     Console.WriteLine($"-> SUCCESS: Found EBML Header (MKV) at offset {mkvSig}");
                                 }
                                 else
                                 {
                                     Console.WriteLine("-> FAIL: Did not find EBML Header (MKV)");
                                 }
                             }
                             catch (Exception ex)
                             {
                                 Console.WriteLine($"DavMultipartFileStream Failed: {ex.Message}");
                             }
                             
                             // Test 4: Inspect Headers
                             Console.WriteLine("\n--- TEST 4: Header Inspection ---");
                             try
                             {
                                 var rawStream = await client.GetSegmentStreamAsync(segmentId, false, CancellationToken.None).ConfigureAwait(false);
                                 
                                 // Read first 50MB into MemoryStream
                                 var ms = new MemoryStream();
                                 var buffer = new byte[65536];
                                 int read;
                                 int totalRead = 0;
                                 int maxBytes = 50 * 1024 * 1024;
                                 
                                 while (totalRead < maxBytes && (read = await rawStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                                 {
                                     ms.Write(buffer, 0, read);
                                     totalRead += read;
                                 }
                                 ms.Position = 0;
                                 Console.WriteLine($"Buffered {totalRead} bytes for header inspection.");
                                 
                                 // Scan for MKV signature in the whole buffer
                                 var fullBuffer = ms.ToArray();
                                 var deepSig = FindMagic(fullBuffer, totalRead, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
                                 if (deepSig >= 0)
                                 {
                                     Console.WriteLine($"-> DEEP SCAN: Found EBML Header (MKV) at absolute offset {deepSig}");
                                 }
                                 else
                                 {
                                     Console.WriteLine("-> DEEP SCAN: Did not find EBML Header (MKV) in first 50MB");
                                 }
                                 ms.Position = 0; // Reset for header factory

                                 var readerOptions = new SharpCompress.Readers.ReaderOptions();
                                 var headerFactory = new SharpCompress.Common.Rar.Headers.RarHeaderFactory(SharpCompress.IO.StreamingMode.Seekable, readerOptions);
                                 
                                 foreach (var header in headerFactory.ReadHeaders(ms))
                                 {
                                     if (header.HeaderType == SharpCompress.Common.Rar.Headers.HeaderType.File)
                                     {
                                         Console.WriteLine($"File Header: {header.GetFileName()}");
                                         Console.WriteLine($"  IsEncrypted: {header.GetIsEncrypted()}");
                                         Console.WriteLine($"  IsSolid: {header.GetIsSolid()}");
                                         Console.WriteLine($"  CompressionMethod: {header.GetCompressionMethod()}");
                                         Console.WriteLine($"  DataStartPosition: {header.GetDataStartPosition()}");
                                         Console.WriteLine($"  AdditionalDataSize: {header.GetAdditionalDataSize()}");
                                         try { Console.WriteLine($"  IsFirstVolume: {header.GetIsFirstVolume()}"); } catch {}
                                         try { Console.WriteLine($"  VolumeNumber: {header.GetVolumeNumber()}"); } catch {}
                                         
                                         // Check for AesParams if encrypted
                                         if (header.GetIsEncrypted())
                                         {
                                             var aes = header.GetAesParams(null); // No password
                                             Console.WriteLine($"  AesParams (No Pass): {(aes == null ? "Null" : "Present")}");
                                         }
                                         
                                         // TEST 5: Extract using SharpCompress
                                         Console.WriteLine("\n--- TEST 5: SharpCompress Extraction ---");
                                         try {
                                             ms.Position = 0; // Reset stream
                                             using var reader = SharpCompress.Readers.Rar.RarReader.Open(ms, new SharpCompress.Readers.ReaderOptions() { Password = null });
                                             while (reader.MoveToNextEntry()) {
                                                 if (!reader.Entry.IsDirectory) {
                                                     Console.WriteLine($"Extracting entry: {reader.Entry.Key}");
                                                     using var entryStream = reader.OpenEntryStream();
                                                     var extractedBuffer = new byte[128];
                                                     var extractedRead = entryStream.Read(extractedBuffer, 0, extractedBuffer.Length);
                                                     Console.WriteLine($"Read {extractedRead} bytes from SharpCompress stream.");
                                                     PrintHex(extractedBuffer, extractedRead);
                                                     
                                                     if (extractedRead >= 4 && extractedBuffer[0] == 0x1A && extractedBuffer[1] == 0x45 && extractedBuffer[2] == 0xDF && extractedBuffer[3] == 0xA3)
                                                     {
                                                         Console.WriteLine("-> SUCCESS: SharpCompress output HAS EBML Header (MKV)!");
                                                         Console.WriteLine("-> CONCLUSION: The file has Filters or Hidden Encryption that SharpCompress handles but raw streaming misses.");
                                                     }
                                                     else
                                                     {
                                                         Console.WriteLine("-> FAIL: SharpCompress output matches Raw output (Garbage).");
                                                     }
                                                     break;
                                                 }
                                             }
                                         } catch (Exception ex) {
                                             Console.WriteLine($"SharpCompress Extraction Failed: {ex.Message}");
                                         }

                                         break; // Just the first file
                                     }
                                 }
                             }
                             catch (Exception ex)
                             {
                                 Console.WriteLine($"Header Inspection Failed: {ex.Message}");
                             }
                         }
                         else
                         {
                             Console.WriteLine("DavMultipartFile found but no parts?");
                             return;
                         }
                    }
                    else
                    {
                        Console.WriteLine("Item found but no file data (Nzb/Rar/Multipart).");
                        return;
                    }
                }
            }
        }
        else
        {
            segmentId = args[argIndex + 1];
        }

        Console.WriteLine($"Testing segment: {segmentId}");
        
        try {
            // Test 1: Raw Stream
            Console.WriteLine("\n--- TEST 1: RAW STREAM ---");
            var rawStream = await client.GetSegmentStreamAsync(segmentId, false, CancellationToken.None).ConfigureAwait(false);
            byte[] rawBuffer = new byte[1024];
            int rawRead = await rawStream.ReadAsync(rawBuffer, 0, rawBuffer.Length).ConfigureAwait(false);
            Console.WriteLine($"Read {rawRead} raw bytes.");
            PrintHex(rawBuffer, 64);

            // Test 2: yEnc Decoded Stream (App Logic)
            Console.WriteLine("\n--- TEST 2: YENC DECODED STREAM (App Logic) ---");
            try {
                var yencStream = await client.GetSegmentStreamAsync(segmentId, true, CancellationToken.None).ConfigureAwait(false);
                byte[] yencBuffer = new byte[16384];
                int totalRead = 0;
                int read;
                while (totalRead < yencBuffer.Length && (read = await yencStream.ReadAsync(yencBuffer, totalRead, yencBuffer.Length - totalRead).ConfigureAwait(false)) > 0)
                {
                    totalRead += read;
                }
                Console.WriteLine($"Read {totalRead} decoded bytes.");
                
                byte[] rar4 = { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00 };
                byte[] rar5 = { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00 };
                byte[] sevenZip = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
                
                Console.WriteLine("Scanning for magics...");
                for (int i = 0; i <= totalRead - 8; i++) {
                    if (IsMatch(yencBuffer, i, rar4)) Console.WriteLine($"- Found RAR4 at offset {i}");
                    if (IsMatch(yencBuffer, i, rar5)) Console.WriteLine($"- Found RAR5 at offset {i}");
                    if (i <= totalRead - 6 && IsMatch(yencBuffer, i, sevenZip)) Console.WriteLine($"- Found 7z at offset {i}");
                }

                PrintHex(yencBuffer, 512);
            } catch (Exception ex) {
                Console.WriteLine($"yEnc Decoding Failed: {ex.Message}");
            }

        } catch (Exception ex) {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static bool IsMatch(byte[] data, int offset, byte[] sequence) {
        for (int i = 0; i < sequence.Length; i++) {
            if (data[offset + i] != sequence[i]) return false;
        }
        return true;
    }

    private static void PrintHex(byte[] buffer, int length)
    {
        int toPrint = Math.Min(length, buffer.Length);
        for (int i = 0; i < (toPrint + 15) / 16; i++)
        {
            var line = buffer.Skip(i * 16).Take(Math.Min(16, toPrint - i * 16)).ToArray();
            Console.WriteLine($"{i * 16:X4}: " + BitConverter.ToString(line).Replace("-", " "));
        }
    }
    
    private static int FindMagic(byte[] data, int length, byte[] sequence)
    {
        for (var i = 0; i <= length - sequence.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (data[i + j] != sequence[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}