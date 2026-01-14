using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Hashing;

namespace NzbWebDAV.Tools;

public class MockNntpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _latencyMs;
    private readonly int _jitterMs;
    private readonly double _timeoutRate;
    private readonly int _segmentSize;
    private bool _running;
    private readonly byte[] _staticArticleBody;
    private readonly byte[] _rarFirstSegmentBody;

    // RAR5 magic bytes: 52 61 72 21 1A 07 01 00
    private static readonly byte[] Rar5Magic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

    // Regex to parse message IDs like: mock-000-000001-<guid>@mock.server
    private static readonly Regex MsgIdPattern = new(@"mock-(\d{3})-(\d{6})-", RegexOptions.Compiled);

    public MockNntpServer(int port, int latencyMs = 150, int segmentSize = 716800, int jitterMs = 40, double timeoutRate = 0.01)
    {
        _latencyMs = latencyMs;
        _jitterMs = jitterMs;
        _timeoutRate = timeoutRate;
        _segmentSize = segmentSize;
        _listener = new TcpListener(IPAddress.Any, port);
        _staticArticleBody = GenerateStaticArticle(segmentSize, rarHeader: false);
        _rarFirstSegmentBody = GenerateStaticArticle(segmentSize, rarHeader: true);
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

                // 2. Simulate Timeout / Stall only for BODY/ARTICLE commands (not auth)
                var isDataCommand = cmd == "BODY" || cmd == "ARTICLE";
                if (isDataCommand && _timeoutRate > 0 && Random.Shared.NextDouble() < _timeoutRate)
                {
                    // Stall for 10 seconds then disconnect (simulates slow/stuck provider)
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
                        await stream.WriteAsync(GetArticleBodyForMsgId(msgId));
                        break;
                    case "ARTICLE":
                        // Format: 220 0 <msgid> article
                        msgId = parts.Length > 1 ? parts[1] : "<unknown>";
                        var fileName = GetFileNameForMsgId(msgId);
                        await writer.WriteLineAsync($"220 0 {msgId} article");
                        await writer.WriteLineAsync($"Message-ID: {msgId}");
                        await writer.WriteLineAsync($"Subject: {fileName}");
                        await writer.WriteLineAsync($"Date: Fri, 09 Jan 2026 12:00:00 GMT");
                        await writer.WriteLineAsync(); // End of headers
                        await stream.WriteAsync(GetArticleBodyForMsgId(msgId));
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
                        var headFileName = GetFileNameForMsgId(msgId);
                        await writer.WriteLineAsync($"221 0 {msgId} article");
                        await writer.WriteLineAsync($"Message-ID: {msgId}");
                        await writer.WriteLineAsync($"Subject: {headFileName}");
                        await writer.WriteLineAsync($"Date: Fri, 09 Jan 2026 12:00:00 GMT");
                        await writer.WriteLineAsync($"Bytes: {_segmentSize}");
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

    /// <summary>
    /// Parse message ID to determine if this is a first segment (needs RAR header)
    /// Format: mock-{volumeIndex:D3}-{segmentIndex:D6}-{guid}@mock.server
    /// </summary>
    private byte[] GetArticleBodyForMsgId(string msgId)
    {
        var match = MsgIdPattern.Match(msgId);
        if (match.Success)
        {
            var segmentIndex = int.Parse(match.Groups[2].Value);
            // First segment of any RAR volume needs RAR magic bytes
            if (segmentIndex == 0)
            {
                return _rarFirstSegmentBody;
            }
        }
        return _staticArticleBody;
    }

    /// <summary>
    /// Get a simulated filename based on message ID
    /// </summary>
    private string GetFileNameForMsgId(string msgId)
    {
        var match = MsgIdPattern.Match(msgId);
        if (match.Success)
        {
            var volumeIndex = int.Parse(match.Groups[1].Value);
            if (volumeIndex == 0)
                return "MockArchive.rar";
            else
                return $"MockArchive.r{(volumeIndex - 1):D2}";
        }
        // Flat file or unknown format
        if (msgId.Contains("mock-flat-"))
            return "Mock_File_1GB.bin";
        return "mock_file.bin";
    }

    private byte[] GenerateStaticArticle(int size, bool rarHeader)
    {
        // 1. Generate decoded data
        var decodedData = new byte[size];

        if (rarHeader)
        {
            // Start with RAR5 magic bytes
            Array.Copy(Rar5Magic, decodedData, Rar5Magic.Length);
            // Fill rest with 'R' for RAR content
            for (int i = Rar5Magic.Length; i < size; i++)
                decodedData[i] = (byte)'R';
        }
        else
        {
            // Fill with 'A' for normal content
            Array.Fill(decodedData, (byte)'A');
        }

        // 2. Calculate CRC of decoded data
        var crc = new Crc32();
        crc.Append(decodedData);
        var hash = BitConverter.ToUInt32(crc.GetCurrentHash());

        // 3. Generate yEnc encoded body
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII);
        writer.NewLine = "\r\n";

        // Header
        var fileName = rarHeader ? "MockArchive.rar" : "mock_file.bin";
        writer.WriteLine($"=ybegin part=1 line=128 size={size} name={fileName}");
        writer.WriteLine($"=ypart begin=1 end={size}");
        writer.Flush();

        // Write header to buffer
        var buffer = new List<byte>();
        buffer.AddRange(ms.ToArray());

        // Body: encode each byte with yEnc encoding
        var crlf = new byte[] { 13, 10 };
        int bytesWritten = 0;
        int linePos = 0;
        var lineBuffer = new List<byte>();

        while (bytesWritten < size)
        {
            var srcByte = decodedData[bytesWritten];
            // yEnc encoding: (byte + 42) mod 256
            var encoded = (byte)((srcByte + 42) % 256);

            // Escape special characters: NUL, LF, CR, =, TAB, SPACE (at line start)
            bool needsEscape = encoded == 0x00 || encoded == 0x0A || encoded == 0x0D ||
                               encoded == 0x3D || // '='
                               (linePos == 0 && (encoded == 0x09 || encoded == 0x20)); // TAB or SPACE at line start

            if (needsEscape)
            {
                lineBuffer.Add((byte)'=');
                lineBuffer.Add((byte)((encoded + 64) % 256));
                linePos += 2;
            }
            else
            {
                lineBuffer.Add(encoded);
                linePos++;
            }

            bytesWritten++;

            // Line wrap at 128 characters or end of data
            if (linePos >= 128 || bytesWritten >= size)
            {
                buffer.AddRange(lineBuffer);
                buffer.AddRange(crlf);
                lineBuffer.Clear();
                linePos = 0;
            }
        }

        // Footer - omit CRC to avoid mismatch issues during mock testing
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
