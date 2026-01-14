using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace NzbWebDAV.Tools;

public class WebDavTester
{
    public static async Task RunAsync(string[] args)
    {
        var argIndex = args.ToList().IndexOf("--test-webdav");

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  WEBDAV PERFORMANCE TESTER");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // Parse arguments
        var baseUrl = "http://localhost:3000";
        var filePath = "";
        var user = "admin";
        var pass = "";
        var downloadKey = ""; // Static download key for token-based auth
        var downloadSize = 0L; // 0 = full file
        var chunkSize = 256 * 1024; // 256KB read chunks
        var seekTest = false;
        var outputFile = ""; // Empty = discard data

        for (int i = argIndex + 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--url=")) baseUrl = arg.Substring(6);
            else if (arg.StartsWith("--file=")) filePath = arg.Substring(7);
            else if (arg.StartsWith("--user=")) user = arg.Substring(7);
            else if (arg.StartsWith("--pass=")) pass = arg.Substring(7);
            else if (arg.StartsWith("--key=")) downloadKey = arg.Substring(6);
            else if (arg.StartsWith("--size=")) downloadSize = long.Parse(arg.Substring(7)) * 1024 * 1024;
            else if (arg.StartsWith("--chunk=")) chunkSize = int.Parse(arg.Substring(8)) * 1024;
            else if (arg.StartsWith("--output=")) outputFile = arg.Substring(9);
            else if (arg == "--seek") seekTest = true;
            else if (!arg.StartsWith("--") && string.IsNullOrEmpty(filePath)) filePath = arg;
        }

        if (string.IsNullOrEmpty(filePath))
        {
            Console.WriteLine("\nUsage: --test-webdav [options] <file-path>");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  --url=<url>       Base URL (default: http://localhost:3000)");
            Console.WriteLine("  --file=<path>     WebDAV file path to download");
            Console.WriteLine("  --user=<user>     WebDAV username (default: admin)");
            Console.WriteLine("  --pass=<pass>     WebDAV password");
            Console.WriteLine("  --key=<key>       Download key for token-based auth (no user/pass needed)");
            Console.WriteLine("  --size=<MB>       Download only first N MB (default: full file)");
            Console.WriteLine("  --chunk=<KB>      Read chunk size in KB (default: 256)");
            Console.WriteLine("  --output=<file>   Save to file (default: discard)");
            Console.WriteLine("  --seek            Run seek/scrubbing tests");
            Console.WriteLine("\nExamples:");
            Console.WriteLine("  --test-webdav --url=http://localhost:3000 --file=/movies/test.mkv --pass=secret");
            Console.WriteLine("  --test-webdav --url=https://nzbdav.example.com --file=/view/content/movie.mkv --key=abc123");
            Console.WriteLine("  --test-webdav --file=/tv/show.mkv --size=100 --seek");
            return;
        }

        Console.WriteLine($"  URL: {baseUrl}");
        Console.WriteLine($"  File: {filePath}");
        Console.WriteLine($"  Auth: {(string.IsNullOrEmpty(downloadKey) ? $"Basic ({user})" : "Download Key")}");
        Console.WriteLine($"  Download Size: {(downloadSize > 0 ? $"{downloadSize / 1024 / 1024} MB" : "Full file")}");
        Console.WriteLine($"  Chunk Size: {chunkSize / 1024} KB");
        Console.WriteLine($"  Seek Test: {seekTest}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

        // Create HTTP client
        var handler = new HttpClientHandler();

        // Only set credentials if using basic auth
        if (string.IsNullOrEmpty(downloadKey) && !string.IsNullOrEmpty(pass))
        {
            handler.Credentials = new NetworkCredential(user, pass);
            handler.PreAuthenticate = true;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        // Add basic auth header if using password auth
        if (string.IsNullOrEmpty(downloadKey) && !string.IsNullOrEmpty(pass))
        {
            var authBytes = Encoding.ASCII.GetBytes($"{user}:{pass}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }

        // Build file URL with optional download key
        var fileUrl = $"{baseUrl.TrimEnd('/')}/{filePath.TrimStart('/')}";
        if (!string.IsNullOrEmpty(downloadKey))
        {
            fileUrl += (fileUrl.Contains('?') ? "&" : "?") + $"downloadKey={downloadKey}";
        }

        try
        {
            // Step 1: Get file info (try HEAD first, fall back to Range GET)
            Console.WriteLine("--- STEP 1: FILE INFO ---");
            var headWatch = Stopwatch.StartNew();
            long contentLength = 0;
            var contentType = "unknown";

            // Try HEAD request first
            var headRequest = new HttpRequestMessage(HttpMethod.Head, fileUrl);
            var headResponse = await client.SendAsync(headRequest);
            headWatch.Stop();

            if (headResponse.IsSuccessStatusCode)
            {
                contentLength = headResponse.Content.Headers.ContentLength ?? 0;
                contentType = headResponse.Content.Headers.ContentType?.ToString() ?? "unknown";
                Console.WriteLine($"HEAD Latency: {headWatch.ElapsedMilliseconds} ms");
            }
            else
            {
                // HEAD failed, try a small range request to get Content-Range header
                Console.WriteLine($"HEAD returned {headResponse.StatusCode}, trying Range GET...");
                headWatch.Restart();
                var rangeRequest = new HttpRequestMessage(HttpMethod.Get, fileUrl);
                rangeRequest.Headers.Range = new RangeHeaderValue(0, 0); // Request just 1 byte
                var rangeResponse = await client.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead);
                headWatch.Stop();

                if (!rangeResponse.IsSuccessStatusCode && rangeResponse.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    Console.WriteLine($"ERROR: Range GET request failed with {rangeResponse.StatusCode}");
                    Console.WriteLine(await rangeResponse.Content.ReadAsStringAsync());
                    return;
                }

                // Parse Content-Range header: bytes 0-0/TOTAL_SIZE
                var contentRange = rangeResponse.Content.Headers.ContentRange;
                if (contentRange?.Length != null)
                {
                    contentLength = contentRange.Length.Value;
                }
                else if (rangeResponse.Content.Headers.ContentLength.HasValue && rangeResponse.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    // Server doesn't support range, returned full content
                    contentLength = rangeResponse.Content.Headers.ContentLength.Value;
                }

                contentType = rangeResponse.Content.Headers.ContentType?.ToString() ?? "unknown";
                Console.WriteLine($"Range GET Latency: {headWatch.ElapsedMilliseconds} ms");
            }

            Console.WriteLine($"File Size: {contentLength / 1024.0 / 1024.0:F2} MB");
            Console.WriteLine($"Content-Type: {contentType}");

            if (contentLength == 0)
            {
                Console.WriteLine("ERROR: File has zero size or Content-Length not provided");
                return;
            }

            var targetSize = downloadSize > 0 ? Math.Min(downloadSize, contentLength) : contentLength;

            // Step 2: Sequential download test
            Console.WriteLine("\n--- STEP 2: SEQUENTIAL DOWNLOAD ---");
            await RunSequentialDownload(client, fileUrl, targetSize, chunkSize, outputFile);

            // Step 3: Seek/scrubbing test
            if (seekTest)
            {
                Console.WriteLine("\n--- STEP 3: SEEK/SCRUBBING TEST ---");
                await RunSeekTest(client, fileUrl, contentLength, chunkSize);
            }

            // Step 4: Range request test
            Console.WriteLine("\n--- STEP 4: RANGE REQUEST LATENCY ---");
            await RunRangeLatencyTest(client, fileUrl, contentLength);

        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static async Task RunSequentialDownload(HttpClient client, string url, long targetSize, int chunkSize, string outputFile)
    {
        var buffer = new byte[chunkSize];
        var totalRead = 0L;
        var readCount = 0;
        var readTimes = new List<double>();

        FileStream? fileStream = null;
        if (!string.IsNullOrEmpty(outputFile))
        {
            fileStream = File.Create(outputFile);
            Console.WriteLine($"Saving to: {outputFile}");
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (targetSize < long.MaxValue)
            {
                request.Headers.Range = new RangeHeaderValue(0, targetSize - 1);
            }

            var overallWatch = Stopwatch.StartNew();
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"ERROR: GET request failed with {response.StatusCode}");
                return;
            }

            var firstByteTime = overallWatch.ElapsedMilliseconds;
            Console.WriteLine($"Time to First Byte: {firstByteTime} ms");

            await using var stream = await response.Content.ReadAsStreamAsync();

            var lastProgressMb = 0L;
            var progressWatch = Stopwatch.StartNew();

            while (totalRead < targetSize)
            {
                var readWatch = Stopwatch.StartNew();
                var bytesToRead = (int)Math.Min(chunkSize, targetSize - totalRead);
                var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead));
                readWatch.Stop();

                if (read == 0) break;

                totalRead += read;
                readCount++;
                readTimes.Add(readWatch.Elapsed.TotalMilliseconds);

                if (fileStream != null)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                }

                // Progress every 10MB
                var currentMb = totalRead / (10 * 1024 * 1024);
                if (currentMb > lastProgressMb)
                {
                    var currentSpeed = (totalRead / 1024.0 / 1024.0) / progressWatch.Elapsed.TotalSeconds;
                    Console.WriteLine($"  Progress: {totalRead / 1024.0 / 1024.0:F1} MB @ {currentSpeed:F2} MB/s (last read: {read / 1024.0:F1} KB in {readWatch.Elapsed.TotalMilliseconds:F1}ms)");
                    lastProgressMb = currentMb;
                }
            }

            overallWatch.Stop();

            // Statistics
            var totalMb = totalRead / 1024.0 / 1024.0;
            var totalSeconds = overallWatch.Elapsed.TotalSeconds;
            var speed = totalMb / totalSeconds;

            readTimes.Sort();
            var avgReadTime = readTimes.Average();
            var medianReadTime = readTimes[readTimes.Count / 2];
            var p95ReadTime = readTimes[(int)(readTimes.Count * 0.95)];
            var maxReadTime = readTimes.Max();
            var minReadTime = readTimes.Min();

            Console.WriteLine($"\nDownload Complete:");
            Console.WriteLine($"  Total: {totalMb:F2} MB in {totalSeconds:F2}s");
            Console.WriteLine($"  Speed: {speed:F2} MB/s");
            Console.WriteLine($"  Time to First Byte: {firstByteTime} ms");
            Console.WriteLine($"\nRead Statistics ({readCount} reads):");
            Console.WriteLine($"  Avg: {avgReadTime:F2}ms");
            Console.WriteLine($"  Median: {medianReadTime:F2}ms");
            Console.WriteLine($"  P95: {p95ReadTime:F2}ms");
            Console.WriteLine($"  Min/Max: {minReadTime:F2}ms / {maxReadTime:F2}ms");

            // Identify bottlenecks
            Console.WriteLine("\nBottleneck Analysis:");
            if (p95ReadTime > 100)
            {
                Console.WriteLine($"  ⚠️  P95 read time ({p95ReadTime:F0}ms) > 100ms - indicates buffer starvation");
            }
            if (maxReadTime > 1000)
            {
                Console.WriteLine($"  ⚠️  Max read time ({maxReadTime:F0}ms) > 1s - indicates stall/timeout");
            }
            if (speed < 10)
            {
                Console.WriteLine($"  ⚠️  Low throughput ({speed:F1} MB/s) - check provider connections");
            }
            if (firstByteTime > 2000)
            {
                Console.WriteLine($"  ⚠️  Slow TTFB ({firstByteTime}ms) - check connection pool warmup");
            }
            if (p95ReadTime <= 100 && maxReadTime <= 1000 && speed >= 10)
            {
                Console.WriteLine("  ✓  No obvious bottlenecks detected");
            }
        }
        finally
        {
            if (fileStream != null)
            {
                await fileStream.DisposeAsync();
            }
        }
    }

    private static async Task RunSeekTest(HttpClient client, string url, long fileSize, int chunkSize)
    {
        var positions = new[] { 0.1, 0.25, 0.5, 0.75, 0.9 };
        var buffer = new byte[chunkSize];
        var seekResults = new List<(double Pct, long SeekMs, long ReadMs, int Bytes)>();

        foreach (var pct in positions)
        {
            var position = (long)(fileSize * pct);
            Console.WriteLine($"\nSeeking to {pct:P0} ({position / 1024.0 / 1024.0:F1} MB)...");

            var seekWatch = Stopwatch.StartNew();

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(position, position + chunkSize - 1);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var headerTime = seekWatch.ElapsedMilliseconds;

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  ERROR: {response.StatusCode}");
                    seekResults.Add((pct, -1, -1, 0));
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                var read = await stream.ReadAsync(buffer, cts.Token);
                seekWatch.Stop();

                var totalTime = seekWatch.ElapsedMilliseconds;
                var readTime = totalTime - headerTime;

                Console.WriteLine($"  Header Time: {headerTime}ms");
                Console.WriteLine($"  Read Time: {readTime}ms");
                Console.WriteLine($"  Total: {totalTime}ms");
                Console.WriteLine($"  Bytes Read: {read}");

                seekResults.Add((pct, headerTime, readTime, read));
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  TIMEOUT (30s)");
                seekResults.Add((pct, -1, -1, 0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.Message}");
                seekResults.Add((pct, -1, -1, 0));
            }
        }

        // Summary
        Console.WriteLine("\n--- SEEK TEST SUMMARY ---");
        foreach (var (pct, seekMs, readMs, bytes) in seekResults)
        {
            var status = seekMs < 0 ? "FAILED" :
                        seekMs + readMs < 1000 ? "GOOD" :
                        seekMs + readMs < 3000 ? "SLOW" : "VERY SLOW";
            Console.WriteLine($"  {pct:P0}: {seekMs + readMs}ms [{status}]");
        }

        var successfulSeeks = seekResults.Where(r => r.SeekMs >= 0).ToList();
        if (successfulSeeks.Count > 0)
        {
            var avgSeek = successfulSeeks.Average(r => r.SeekMs + r.ReadMs);
            Console.WriteLine($"\n  Average Seek Time: {avgSeek:F0}ms");

            if (avgSeek > 3000)
            {
                Console.WriteLine("  ⚠️  Seek latency very high - check buffer/connection settings");
            }
        }
    }

    private static async Task RunRangeLatencyTest(HttpClient client, string url, long fileSize)
    {
        var positions = new[] { 0L, fileSize / 4, fileSize / 2, fileSize * 3 / 4, fileSize - 1024 };
        var latencies = new List<long>();

        Console.WriteLine("Testing range request latency (header only)...\n");

        foreach (var pos in positions)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(pos, pos + 1023);

            var watch = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                watch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    latencies.Add(watch.ElapsedMilliseconds);
                    Console.WriteLine($"  Position {pos / 1024.0 / 1024.0:F1}MB: {watch.ElapsedMilliseconds}ms");
                }
                else
                {
                    Console.WriteLine($"  Position {pos / 1024.0 / 1024.0:F1}MB: ERROR {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Position {pos / 1024.0 / 1024.0:F1}MB: ERROR {ex.Message}");
            }
        }

        if (latencies.Count > 0)
        {
            Console.WriteLine($"\n  Average Latency: {latencies.Average():F0}ms");
            Console.WriteLine($"  Min/Max: {latencies.Min()}ms / {latencies.Max()}ms");
        }
    }
}
