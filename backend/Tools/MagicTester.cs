
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
        Console.WriteLine($"Fetching segment: {segmentId}");
        
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
            var stream = await client.GetSegmentStreamAsync(segmentId, false, CancellationToken.None).ConfigureAwait(false);
            byte[] buffer = new byte[16384];
            int read = 0;
            int totalRead = 0;
            while (totalRead < buffer.Length && (read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
            }
            
            Console.WriteLine($"Read {totalRead} bytes.");
            
            byte[] rar4 = { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00 };
            byte[] rar5 = { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00 };
            byte[] matroska = { 0x1a, 0x45, 0xdf, 0xa3 };
            
            int offset4 = FindMagic(buffer, totalRead, rar4);
            int offset5 = FindMagic(buffer, totalRead, rar5);
            int offsetMkv = FindMagic(buffer, totalRead, matroska);
            
            Console.WriteLine($"RAR4 Offset: {offset4}");
            Console.WriteLine($"RAR5 Offset: {offset5}");
            Console.WriteLine($"MKV Offset: {offsetMkv}");
            
            if (totalRead > 128) {
                Console.WriteLine("First 128 bytes hex:");
                for (int i = 0; i < 8; i++)
                {
                    var line = buffer.Skip(i * 16).Take(16).ToArray();
                    Console.WriteLine($"{i * 16:X4}: " + BitConverter.ToString(line).Replace("-", " "));
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
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
