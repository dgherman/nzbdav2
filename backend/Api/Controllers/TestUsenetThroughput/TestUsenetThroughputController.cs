using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Usenet.Nzb;

namespace NzbWebDAV.Api.Controllers.TestUsenetThroughput;

[ApiController]
[Route("api/test-usenet-throughput")]
public class TestUsenetThroughputController(ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestUsenetThroughputRequest(HttpContext);
        var connectionDetails = request.ToConnectionDetails();
        
        // 1. Create Connection Infrastructure
        // We use the configured connections-per-stream, capped by the provider's max connections
        var connectionsPerStream = configManager.GetConnectionsPerStream();
        var testConnections = Math.Min(connectionDetails.MaxConnections, connectionsPerStream);
        
        var semaphore = new ExtendedSemaphoreSlim(testConnections, testConnections);
        
        var pool = new ConnectionPool<INntpClient>(
            testConnections,
            semaphore,
            ct => UsenetStreamingClient.CreateNewConnection(connectionDetails, null, -1, ct)
        );

        // No global limiter for this isolated test
        var client = new MultiConnectionNntpClient(pool, connectionDetails.Type, null, null, null, -1, connectionDetails.Host);

        try
        {
            Serilog.Log.Information($"[ThroughputTest] Starting test for {connectionDetails.Host} with {testConnections} connections (Limit: {connectionsPerStream}).");

            // 2. Load NZB from upload
            var file = HttpContext.Request.Form.Files["nzbFile"];
            if (file == null)
            {
                Serilog.Log.Warning("[ThroughputTest] No NZB file uploaded.");
                return Ok(new TestUsenetThroughputResponse 
                { 
                    Success = false, 
                    Message = "No NZB file uploaded. Please select an NZB file to test throughput." 
                });
            }
            Serilog.Log.Information($"[ThroughputTest] NZB uploaded: {file.FileName} ({file.Length} bytes)");

            NzbDocument nzb;
            using (var nzbStream = file.OpenReadStream())
            {
                nzb = NzbDocument.Load(nzbStream);
            }

            var nzbFile = nzb.Files.OrderByDescending(f => f.GetTotalYencodedSize()).FirstOrDefault();
            
            if (nzbFile == null)
            {
                Serilog.Log.Warning("[ThroughputTest] No files found in NZB.");
                return Ok(new TestUsenetThroughputResponse 
                { 
                    Success = false, 
                    Message = "No files found in speed-test.nzb" 
                });
            }

            var fileSize = nzbFile.GetTotalYencodedSize();
            
            // 3. Start Download Test
            // Use BufferedSegmentStream with the configured connections
            var segmentIds = nzbFile.GetSegmentIds();
            Serilog.Log.Information($"[ThroughputTest] Parsed NZB. Selected file size: {fileSize} bytes. Segments: {segmentIds.Length}");

            // Use a reasonable buffer size (e.g. 2x connections)
            var bufferSize = Math.Max(20, testConnections * 2);
            
            var cts = new CancellationTokenSource();
            // Timeout after 60 seconds to avoid hanging forever if slow
            cts.CancelAfter(TimeSpan.FromSeconds(60)); 

            await using var stream = new BufferedSegmentStream(
                segmentIds,
                fileSize,
                client,
                testConnections,
                bufferSize,
                cts.Token
            );

            var buffer = new byte[81920]; // 80KB read buffer
            long totalRead = 0;
            var sw = Stopwatch.StartNew();

            // Download for up to 10 seconds or 100MB, whichever comes first, 
            // but ensure we download enough to get a stable speed.
            // Let's try to download for 15 seconds to let TCP ramp up.
            var testDuration = TimeSpan.FromSeconds(15);
            
            Serilog.Log.Information("[ThroughputTest] Starting BufferedSegmentStream download loop...");
            while (sw.Elapsed < testDuration && totalRead < fileSize)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) break;
                totalRead += read;
            }
            
            sw.Stop();
            Serilog.Log.Information($"[ThroughputTest] Download loop finished. TotalRead: {totalRead}. Time: {sw.Elapsed}");

            // 4. Calculate Speed
            var seconds = sw.Elapsed.TotalSeconds;
            if (seconds <= 0) seconds = 0.001; // avoid div/0
            
            var mbRead = totalRead / (1024.0 * 1024.0);
            var speed = mbRead / seconds;

            Serilog.Log.Information($"[ThroughputTest] Completed: {speed:F2} MB/s");

            return Ok(new TestUsenetThroughputResponse
            {
                Success = true,
                SpeedInMBps = Math.Round(speed, 2),
                Message = $"Downloaded {mbRead:F2} MB in {seconds:F2}s using {testConnections} connections."
            });

        }
        catch (OperationCanceledException)
        {
            Serilog.Log.Warning("[ThroughputTest] Timed out.");
            return Ok(new TestUsenetThroughputResponse
            {
                Success = false,
                Message = "Test timed out. Connection ramp-up or download was too slow."
            });
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "[ThroughputTest] Failed.");
            return Ok(new TestUsenetThroughputResponse
            {
                Success = false,
                Message = $"Test failed: {e.Message}"
            });
        }
        finally
        {
            // Cleanup
            pool.Dispose(); // This disposes the semaphore too
        }
    }
}