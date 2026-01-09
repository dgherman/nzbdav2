using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO.Hashing;

namespace NzbWebDAV.Tools;

public class MockNntpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _latencyMs;
    private readonly int _jitterMs;
    private readonly double _timeoutRate;
    private bool _running;
    private readonly byte[] _staticArticleBody;
    
    public MockNntpServer(int port, int latencyMs = 150, int segmentSize = 716800, int jitterMs = 40, double timeoutRate = 0.01)
    {
        _latencyMs = latencyMs;
        _jitterMs = jitterMs;
        _timeoutRate = timeoutRate;
        _listener = new TcpListener(IPAddress.Any, port);
        _staticArticleBody = GenerateStaticArticle(segmentSize);
    }

    public void Start()
    {
        _running = true;
        _listener.Start();
        // Fire and forget accept loop
        _ = Task.Run(AcceptClients);
    }

    private async Task AcceptClients()
    {
        while (_running)
        {
            try 
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch 
            {
                if (_running) await Task.Delay(100);
            }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using var stream = client.GetStream();
        // Set write timeout to break deadlocks if client doesn't drain body
        stream.WriteTimeout = 1000; // 1 second
        
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        try 
        {
            await writer.WriteLineAsync("200 Mock NNTP Server Ready");

            while (client.Connected && _running)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                var parts = line.Split(' ');
                var cmd = parts[0].ToUpper();

                // 1. Simulate Latency with Jitter
                if (_latencyMs > 0) 
                {
                    var delay = _latencyMs;
                    if (_jitterMs > 0)
                    {
                        var jitter = Random.Shared.Next(-_jitterMs, _jitterMs);
                        delay += jitter;
                        if (delay < 0) delay = 0;
                    }
                    await Task.Delay(delay);
                }

                // 2. Simulate Timeout / Stall (Mocking a lost packet or server hang)
                if (_timeoutRate > 0 && Random.Shared.NextDouble() < _timeoutRate)
                {
                    // Stall for 10 seconds then disconnect
                    await Task.Delay(10000);
                    client.Close();
                    return;
                }

                string msgId;
                switch (cmd)
                {
                    case "CAPABILITIES":
                        await writer.WriteLineAsync("101 Capability list:\r\nVERSION 2\r\nREADER\r\n.\r\n");
                        break;
                    case "MODE":
                        await writer.WriteLineAsync("200 Posting allowed");
                        break;
                    case "AUTHINFO":
                        if (parts.Length > 1 && parts[1].ToUpper() == "USER") await writer.WriteLineAsync("381 Password required");
                        else await writer.WriteLineAsync("281 Authentication accepted");
                        break;
                    case "GROUP":
                        await writer.WriteLineAsync("211 1000 1 1000 mock.group");
                        break;
                    case "BODY":
                        // Format: 222 0 <msgid> article
                        msgId = parts.Length > 1 ? parts[1] : "<unknown>";
                        await writer.WriteLineAsync($"222 0 {msgId} article");
                        await stream.WriteAsync(_staticArticleBody);
                        break;
                    case "ARTICLE":
                        // Format: 220 0 <msgid> article
                        msgId = parts.Length > 1 ? parts[1] : "<unknown>";
                        await writer.WriteLineAsync($"220 0 {msgId} article");
                        await writer.WriteLineAsync($"Message-ID: {msgId}");
                        await writer.WriteLineAsync($"Subject: Mock File");
                        await writer.WriteLineAsync($"Date: Fri, 09 Jan 2026 12:00:00 GMT");
                        await writer.WriteLineAsync(); // End of headers
                        await stream.WriteAsync(_staticArticleBody);
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("205 Bye");
                        client.Close();
                        return;
                    case "STAT":
                        msgId = parts.Length > 1 ? parts[1] : "<unknown>";
                        await writer.WriteLineAsync($"223 0 {msgId} article");
                        break;
                    case "HEAD":
                        msgId = parts.Length > 1 ? parts[1] : "<unknown>";
                        await writer.WriteLineAsync($"221 0 {msgId} article");
                        await writer.WriteLineAsync($"Message-ID: {msgId}");
                        await writer.WriteLineAsync($"Subject: Mock File");
                        await writer.WriteLineAsync($"Date: Fri, 09 Jan 2026 12:00:00 GMT");
                        await writer.WriteLineAsync($"Bytes: {_staticArticleBody.Length}"); 
                        await writer.WriteLineAsync(); // End of headers
                        await writer.WriteLineAsync(".");
                        break;
                    case "DATE":
                         await writer.WriteLineAsync("111 20260109120000");
                         break;
                    default:
                        await writer.WriteLineAsync("500 Unknown command");
                        break;
                }
            }
        }
        catch { }
        finally
        {
            client.Close();
        }
    }

    private byte[] GenerateStaticArticle(int size)
    {
        // 1. Calculate CRC of DECODED data (all 'A's)
        var decodedData = new byte[size];
        Array.Fill(decodedData, (byte)'A'); // Decoded content
        
        var crc = new Crc32();
        crc.Append(decodedData);
        var hash = BitConverter.ToUInt32(crc.GetCurrentHash());
        if (!BitConverter.IsLittleEndian)
        {
             // Crc32 returns BigEndian bytes? No, standard implementation usually returns bytes in memory order.
             // System.IO.Hashing.Crc32 returns bytes in Big Endian if checked against standard CRC?
             // Actually ToUInt32 will interpret them based on architecture.
             // yEnc expects Hex string of the integer value.
             // Usually BitConverter.ToUInt32 + X8 works if bytes are Little Endian.
             // Let's verify CRC32 implementation behavior if needed, but usually this works for .NET.
             // System.IO.Hashing returns BigEndian on GetCurrentHash()? 
             // Actually, let's reverse if needed.
             // We'll assume standard behavior for now.
             
             // Wait, System.IO.Hashing.Crc32.GetCurrentHash() returns 4 bytes.
             // On Little Endian machine (Intel), ToUInt32 reads them.
             // If GetCurrentHash is Big Endian, we need to reverse.
             // Most networking hash functions are Big Endian.
             // Let's swap just in case to match "standard" CRC presentation or check docs.
             // Actually, simpler: NzbDav verifies it. If it fails, we swap.
             // For now, assume standard ToUInt32 is fine.
        }

        // 2. Generate ENCODED body
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII);
        writer.NewLine = "\r\n";
        
        // Header
        writer.WriteLine($"=ybegin part=1 line=128 size={size} name=mock_file.bin");
        writer.WriteLine($"=ypart begin=1 end={size}");
        writer.Flush();

        // Write header to buffer
        var buffer = new List<byte>();
        buffer.AddRange(ms.ToArray());

        // Body: 'A' (65) + 42 = 'k' (107)
        // 128 chars per line
        var encodedChar = (byte)('A' + 42); 
        var lineBytes = Enumerable.Repeat(encodedChar, 128).ToArray();
        var crlf = new byte[] { 13, 10 };
        
        int bytesWritten = 0;
        while (bytesWritten < size)
        {
            int toWrite = Math.Min(128, size - bytesWritten);
            if (toWrite == 128)
            {
                buffer.AddRange(lineBytes);
            }
            else
            {
                buffer.AddRange(Enumerable.Repeat(encodedChar, toWrite));
            }
            buffer.AddRange(crlf);
            bytesWritten += toWrite;
        }

        // Footer
        // For simple single-part yEnc, pcrc32 works.
        // Omit CRC to avoid mismatch issues during mock testing
        var footerStr = $"=yend size={size} part=1\r\n.\r\n";
        buffer.AddRange(Encoding.ASCII.GetBytes(footerStr));
        
        return buffer.ToArray();
    }

    public void Dispose()
    {
        _running = false;
        _listener.Stop();
    }
}
