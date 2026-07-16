using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NzbWebDAV.Tools;

/// <summary>
/// In-process NNTP server that serves synthetic articles for benchmarking.
///
/// Every article it serves must carry the yEnc part header for *that* segment. Serving one
/// hardcoded "=ypart begin=1 end=<segmentSize>" for all segments makes every segment claim to be
/// byte 0 of the file, so the client computes a file size of one segment no matter how many the
/// NZB lists — the benchmark then streams a single segment and reports meaningless numbers.
/// The layout registry below exists to keep the headers honest.
/// </summary>
public class MockNntpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _latencyMs;
    private readonly int _jitterMs;
    private readonly double _timeoutRate;
    private readonly int _segmentSize;
    private bool _running;

    /// <summary>msgId (without angle brackets) to the article that segment must return.</summary>
    private readonly Dictionary<string, SegmentLayout> _layouts = new();

    /// <summary>
    /// Encoded payloads keyed by (size, rarHeader). Segments of equal shape share one array: the
    /// server runs in the benchmark's own process, so per-request payload allocation would land in
    /// the GC counters the benchmark reports and be indistinguishable from client-side garbage.
    /// </summary>
    private readonly ConcurrentDictionary<(int Size, bool Rar), byte[]> _payloadCache = new();

    private readonly SegmentLayout _fallbackLayout;

    // RAR5 magic bytes: 52 61 72 21 1A 07 01 00
    private static readonly byte[] Rar5Magic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

    private sealed class SegmentLayout
    {
        public required string FileName { get; init; }
        public required int PartNumber { get; init; }
        public required int PartSize { get; init; }
        public required byte[] HeaderBytes { get; init; }
        public required byte[] FooterBytes { get; init; }
        public required byte[] Payload { get; init; }
    }

    public MockNntpServer(
        int port,
        int latencyMs = 150,
        int segmentSize = 716800,
        int jitterMs = 40,
        double timeoutRate = 0.01,
        MockNzbGenerator.GenerationResult? layout = null)
    {
        _latencyMs = latencyMs;
        _jitterMs = jitterMs;
        _timeoutRate = timeoutRate;
        _segmentSize = segmentSize;
        _listener = new TcpListener(IPAddress.Any, port);

        _fallbackLayout = BuildLayout("mock_file.bin", partNumber: 1, totalParts: 1,
            fileSize: segmentSize, partOffset: 0, partSize: segmentSize, rarHeader: false);

        if (layout != null) RegisterLayout(layout);
    }

    /// <summary>
    /// Teach the server the real geometry of the generated NZB, so each segment gets a truthful
    /// =ybegin/=ypart pair.
    /// </summary>
    private void RegisterLayout(MockNzbGenerator.GenerationResult layout)
    {
        foreach (var file in layout.Files)
        {
            for (var i = 0; i < file.SegmentIds.Count; i++)
            {
                var partSize = MockNzbGenerator.SegmentSizeAt(i, file.SegmentCount, file.FileSize, _segmentSize);
                _layouts[file.SegmentIds[i]] = BuildLayout(
                    file.FileName,
                    partNumber: i + 1,
                    totalParts: file.SegmentCount,
                    fileSize: file.FileSize,
                    partOffset: (long)i * _segmentSize,
                    partSize: partSize,
                    // Only the first part of a RAR volume carries the archive magic.
                    rarHeader: file.IsRar && i == 0);
            }
        }
    }

    private SegmentLayout BuildLayout(
        string fileName, int partNumber, int totalParts, long fileSize,
        long partOffset, int partSize, bool rarHeader)
    {
        // yEnc is 1-based and inclusive on both ends; the client recovers the segment's place in the
        // file as (begin - 1) and its length as (end - begin + 1).
        var begin = partOffset + 1;
        var end = partOffset + partSize;

        var header = $"=ybegin part={partNumber} total={totalParts} line=128 size={fileSize} name={fileName}\r\n" +
                     $"=ypart begin={begin} end={end}\r\n";
        // CRC is omitted deliberately: the payload is synthetic and the client does not verify it.
        var footer = $"=yend size={partSize} part={partNumber}\r\n.\r\n";

        return new SegmentLayout
        {
            FileName = fileName,
            PartNumber = partNumber,
            PartSize = partSize,
            HeaderBytes = Encoding.ASCII.GetBytes(header),
            FooterBytes = Encoding.ASCII.GetBytes(footer),
            Payload = _payloadCache.GetOrAdd((partSize, rarHeader), k => EncodePayload(k.Size, k.Rar)),
        };
    }

    private static string NormalizeMsgId(string raw) => raw.Trim('<', '>');

    private SegmentLayout GetLayout(string msgId) =>
        _layouts.TryGetValue(NormalizeMsgId(msgId), out var layout) ? layout : _fallbackLayout;

    /// <summary>Port actually bound. Meaningful only after <see cref="Start"/>; pass port 0 to let the OS pick.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

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
        using var reader = new StreamReader(stream, Encoding.ASCII);
        // NNTP terminates every line with CRLF. StreamWriter defaults to Environment.NewLine,
        // which is a bare LF on Linux — where this benchmark actually runs.
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

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
                SegmentLayout layout;
                switch (cmd)
                {
                    case "CAPABILITIES":
                        // Multi-line block, written verbatim: WriteLine would append a second
                        // terminator after the "." and leave a stray blank line in the stream for
                        // the client to read as the next response.
                        await writer.WriteAsync("101 Capability list:\r\nVERSION 2\r\nREADER\r\n.\r\n");
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
                        await WriteArticleBodyAsync(stream, GetLayout(msgId));
                        break;
                    case "ARTICLE":
                        // Format: 220 0 <msgid> article
                        msgId = parts.Length > 1 ? parts[1] : "<unknown>";
                        layout = GetLayout(msgId);
                        await writer.WriteLineAsync($"220 0 {msgId} article");
                        await writer.WriteLineAsync($"Message-ID: {msgId}");
                        await writer.WriteLineAsync($"Subject: {layout.FileName}");
                        await writer.WriteLineAsync($"Date: Fri, 09 Jan 2026 12:00:00 GMT");
                        await writer.WriteLineAsync(); // End of headers
                        await WriteArticleBodyAsync(stream, layout);
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
                        layout = GetLayout(msgId);
                        await writer.WriteLineAsync($"221 0 {msgId} article");
                        await writer.WriteLineAsync($"Message-ID: {msgId}");
                        await writer.WriteLineAsync($"Subject: {layout.FileName}");
                        await writer.WriteLineAsync($"Date: Fri, 09 Jan 2026 12:00:00 GMT");
                        await writer.WriteLineAsync($"Bytes: {layout.PartSize}");
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

    private static async Task WriteArticleBodyAsync(NetworkStream stream, SegmentLayout layout)
    {
        await stream.WriteAsync(layout.HeaderBytes);
        await stream.WriteAsync(layout.Payload);
        await stream.WriteAsync(layout.FooterBytes);
    }

    /// <summary>
    /// yEnc-encode <paramref name="size"/> bytes of synthetic data, returning only the encoded data
    /// lines — the =ybegin/=ypart/=yend framing is per-segment and lives in <see cref="SegmentLayout"/>.
    /// </summary>
    private static byte[] EncodePayload(int size, bool rarHeader)
    {
        var decodedData = new byte[size];

        if (rarHeader)
        {
            // Start with RAR5 magic bytes
            Array.Copy(Rar5Magic, decodedData, Math.Min(Rar5Magic.Length, size));
            // Fill rest with 'R' for RAR content
            for (int i = Rar5Magic.Length; i < size; i++)
                decodedData[i] = (byte)'R';
        }
        else
        {
            // Fill with 'A' for normal content
            Array.Fill(decodedData, (byte)'A');
        }

        var buffer = new List<byte>();
        var crlf = new byte[] { 13, 10 };
        int bytesWritten = 0;
        int linePos = 0;
        var lineBuffer = new List<byte>();

        while (bytesWritten < size)
        {
            var srcByte = decodedData[bytesWritten];
            // yEnc encoding: (byte + 42) mod 256
            var encoded = (byte)((srcByte + 42) % 256);

            // Escape special characters: NUL, LF, CR, =, TAB/SPACE/'.' at line start.
            // A leading '.' would otherwise need NNTP dot-stuffing; escaping it here keeps the
            // wire format unambiguous whatever the synthetic payload happens to encode to.
            bool needsEscape = encoded == 0x00 || encoded == 0x0A || encoded == 0x0D ||
                               encoded == 0x3D || // '='
                               (linePos == 0 && (encoded == 0x09 || encoded == 0x20 || encoded == 0x2E));

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

        return buffer.ToArray();
    }

    public void Dispose()
    {
        _running = false;
        _listener.Stop();
    }
}
