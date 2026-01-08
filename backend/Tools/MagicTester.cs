using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Tools;

public class MagicTester
{
    public static async Task RunAsync(string[] args)
    {
        var argIndex = args.ToList().IndexOf("--magic-test");
        if (args.Length <= argIndex + 1)
        {
            Console.WriteLine("Usage: --magic-test <segmentId>");
            return;
        }
        
        string segmentId = args[argIndex + 1];
        Console.WriteLine($"Testing segment: {segmentId}");
        
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
                PrintHex(yencBuffer, 128);
                
                byte[] rar4 = { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00 };
                byte[] rar5 = { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00 };
                
                Console.WriteLine($"RAR4 Magic found: {FindMagic(yencBuffer, totalRead, rar4) != -1}");
                Console.WriteLine($"RAR5 Magic found: {FindMagic(yencBuffer, totalRead, rar5) != -1}");
            } catch (Exception ex) {
                Console.WriteLine($"yEnc Decoding Failed: {ex.Message}");
            }

        } catch (Exception ex) {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
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