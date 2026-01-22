using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Api.Controllers.ProviderBenchmark;

[ApiController]
[Route("api/provider-benchmark")]
public class ProviderBenchmarkController(
    UsenetStreamingClient usenetClient,
    DavDatabaseContext dbContext,
    WebsocketManager websocketManager,
    NzbProviderAffinityService affinityService
) : ControllerBase
{
    private const int TestSizeMb = 200;
    private const long MinFileSizeBytes = 1L * 1024 * 1024 * 1024; // 1GB minimum
    private const int ConnectionsPerTest = 20;

    [HttpGet]
    public async Task<IActionResult> GetHistory()
    {
        return await GetBenchmarkHistory().ConfigureAwait(false);
    }

    [HttpGet("providers")]
    public IActionResult GetProviders()
    {
        var response = new ProviderListResponse { Status = true };

        try
        {
            var allProviders = usenetClient.GetProviderInfo();
            response.Providers = allProviders.Select(p => new ProviderInfoDto
            {
                Index = p.Index,
                Host = p.Host,
                Type = p.Type,
                MaxConnections = p.MaxConnections,
                IsDisabled = p.Type == "Disabled"
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Benchmark] Error getting provider list");
            response.Status = false;
            response.Error = ex.Message;
        }

        return Ok(response);
    }

    [HttpPost]
    public async Task<IActionResult> RunBenchmarkPost([FromBody] ProviderBenchmarkRequest? request)
    {
        // Get all providers first (needed for availability check)
        var allProviders = usenetClient.GetProviderInfo().ToList();

        // Filter providers based on request
        List<(int Index, string Host, string Type, int MaxConnections)> providers;
        if (request?.ProviderIndices != null && request.ProviderIndices.Count > 0)
        {
            providers = allProviders
                .Where(p => request.ProviderIndices.Contains(p.Index))
                .ToList();
        }
        else
        {
            providers = allProviders
                .Where(p => p.Type != "Disabled")
                .ToList();
        }

        if (providers.Count == 0)
        {
            Log.Warning("[Benchmark] No providers selected for testing");
            return Ok(new ProviderBenchmarkResponse
            {
                Status = false,
                Error = "No providers selected for testing."
            });
        }

        // Find a test file - use provided FileId or find a random one
        // If a file's first segment is unavailable on ALL providers, try a different file
        const int MaxFileAttempts = 5;
        Guid? fileId = request?.FileId;
        (string FileName, string[] SegmentIds, long FileSize, Guid FileId, long[]? SegmentSizes)? testFile = null;
        var triedFileIds = new HashSet<Guid>();

        using var availabilityCts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // Overall timeout for file selection

        for (int attempt = 0; attempt < MaxFileAttempts; attempt++)
        {
            if (fileId.HasValue && attempt == 0)
            {
                // User requested a specific file - try to get it first
                testFile = await GetFileById(fileId.Value).ConfigureAwait(false);
                if (testFile == null)
                {
                    Log.Warning("[Benchmark] Requested file {FileId} not found, falling back to random selection", fileId.Value);
                }
                else
                {
                    triedFileIds.Add(testFile.Value.FileId);
                }
            }

            if (testFile == null)
            {
                testFile = await FindRandomLargeFile(triedFileIds).ConfigureAwait(false);
            }

            if (testFile == null)
            {
                Log.Warning("[Benchmark] No more suitable test files found (attempt {Attempt}/{Max})", attempt + 1, MaxFileAttempts);
                break;
            }

            // Check if this file is available on ALL selected providers
            var (allAvailable, availableProviders, unavailableProviders) = await CheckFileAvailabilityOnProviders(
                testFile.Value.SegmentIds,
                testFile.Value.FileName,
                providers,
                availabilityCts.Token
            ).ConfigureAwait(false);

            if (allAvailable)
            {
                Log.Information("[Benchmark] File {FileName} available on all {Count} providers (attempt {Attempt})",
                    testFile.Value.FileName, providers.Count, attempt + 1);
                break;
            }

            // File not available on all providers - try a different one
            Log.Warning("[Benchmark] File {FileName} only available on {Available}/{Total} providers (attempt {Attempt}/{Max}), trying another",
                testFile.Value.FileName, availableProviders.Count, providers.Count, attempt + 1, MaxFileAttempts);
            triedFileIds.Add(testFile.Value.FileId);
            testFile = null;
        }

        if (testFile == null)
        {
            Log.Warning("[Benchmark] No suitable test file found after {Attempts} attempts (>= {MinSize} GB, available on all providers)",
                MaxFileAttempts, MinFileSizeBytes / 1024.0 / 1024.0 / 1024.0);
            return Ok(new ProviderBenchmarkResponse
            {
                Status = false,
                Error = $"No NZB file >= 1GB found that is available on all {providers.Count} selected provider(s) after trying {triedFileIds.Count} file(s)."
            });
        }

        var (fileName, segmentIds, fileSize, testFileId, segmentSizes) = testFile.Value;

        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var includeLoadBalanced = request?.IncludeLoadBalanced ?? true;

        // Calculate total providers to test (individual + load-balanced if applicable)
        var totalProviders = providers.Count + (includeLoadBalanced && providers.Count > 1 ? 1 : 0);

        Log.Information("[Benchmark] Starting background benchmark: {FileName} ({Size:F2} GB), testing {Count} providers",
            fileName, fileSize / 1024.0 / 1024.0 / 1024.0, totalProviders);

        // Start the benchmark in a background task (NOT tied to HTTP request)
        _ = Task.Run(async () =>
        {
            await RunBenchmarkBackground(
                runId, createdAt, fileName, segmentIds, fileSize, providers,
                includeLoadBalanced, totalProviders, segmentSizes
            ).ConfigureAwait(false);
        });

        // Return immediately with "started" response
        return Ok(new ProviderBenchmarkResponse
        {
            Status = true,
            RunId = runId,
            CreatedAt = createdAt,
            TestFileName = fileName,
            TestFileSize = fileSize,
            TestSizeMb = TestSizeMb,
            TotalProviders = totalProviders,
            IsComplete = false, // Benchmark is running in background
            TestFileId = testFileId
        });
    }

    /// <summary>
    /// Runs the benchmark in a background task, sending progress via WebSocket.
    /// Not tied to HTTP request lifecycle.
    /// </summary>
    private async Task RunBenchmarkBackground(
        Guid runId,
        DateTimeOffset createdAt,
        string fileName,
        string[] segmentIds,
        long fileSize,
        List<(int Index, string Host, string Type, int MaxConnections)> providers,
        bool includeLoadBalanced,
        int totalProviders,
        long[]? segmentSizes)
    {
        var response = new ProviderBenchmarkResponse
        {
            Status = true,
            RunId = runId,
            CreatedAt = createdAt,
            TestFileName = fileName,
            TestFileSize = fileSize,
            TestSizeMb = TestSizeMb,
            TotalProviders = totalProviders,
            IsComplete = false
        };

        // Use a separate cancellation token for the benchmark (10 minute overall timeout)
        using var benchmarkCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        try
        {
            Log.Information("[Benchmark] Background task started for RunId: {RunId}", runId);

            var dbResults = new List<ProviderBenchmarkResult>();

            // Test each provider individually
            var providerNum = 0;
            foreach (var provider in providers)
            {
                providerNum++;
                Log.Information("[Benchmark] Testing provider {Num}/{Total}: {Host}",
                    providerNum, providers.Count, provider.Host);

                var result = await TestProvider(
                    provider.Index,
                    provider.Host,
                    provider.Type,
                    segmentIds,
                    fileSize,
                    fileName,
                    isLoadBalanced: false,
                    segmentSizes,
                    benchmarkCts.Token
                ).ConfigureAwait(false);

                response.Results.Add(result);

                if (result.Success)
                {
                    Log.Information("[Benchmark] Provider {Host} completed: {Speed:F2} MB/s",
                        provider.Host, result.SpeedMbps);
                }
                else
                {
                    Log.Warning("[Benchmark] Provider {Host} failed: {Error}",
                        provider.Host, result.ErrorMessage);
                }

                // Send WebSocket update for real-time UI
                await SendBenchmarkProgress(response).ConfigureAwait(false);

                // Create database record
                dbResults.Add(new ProviderBenchmarkResult
                {
                    RunId = runId,
                    CreatedAt = createdAt,
                    TestFileName = fileName,
                    TestFileSize = fileSize,
                    TestSizeMb = TestSizeMb,
                    ProviderIndex = result.ProviderIndex,
                    ProviderHost = result.ProviderHost,
                    ProviderType = result.ProviderType,
                    IsLoadBalanced = result.IsLoadBalanced,
                    BytesDownloaded = result.BytesDownloaded,
                    ElapsedSeconds = result.ElapsedSeconds,
                    SpeedMbps = result.SpeedMbps,
                    Success = result.Success,
                    ErrorMessage = result.ErrorMessage
                });
            }

            // Test load-balanced (NZB Affinity) - only if requested and multiple providers
            if (includeLoadBalanced && providers.Count > 1)
            {
                Log.Information("[Benchmark] Testing load-balanced mode (all {Count} providers combined)",
                    providers.Count);

                var lbResult = await TestProvider(
                    providerIndex: -1,
                    providerHost: "All Providers (Load Balanced)",
                    providerType: "Affinity",
                    segmentIds,
                    fileSize,
                    fileName,
                    isLoadBalanced: true,
                    segmentSizes,
                    benchmarkCts.Token
                ).ConfigureAwait(false);

                response.Results.Add(lbResult);

                if (lbResult.Success)
                {
                    Log.Information("[Benchmark] Load-balanced test completed: {Speed:F2} MB/s", lbResult.SpeedMbps);
                }
                else
                {
                    Log.Warning("[Benchmark] Load-balanced test failed: {Error}", lbResult.ErrorMessage);
                }

                dbResults.Add(new ProviderBenchmarkResult
                {
                    RunId = runId,
                    CreatedAt = createdAt,
                    TestFileName = fileName,
                    TestFileSize = fileSize,
                    TestSizeMb = TestSizeMb,
                    ProviderIndex = lbResult.ProviderIndex,
                    ProviderHost = lbResult.ProviderHost,
                    ProviderType = lbResult.ProviderType,
                    IsLoadBalanced = lbResult.IsLoadBalanced,
                    BytesDownloaded = lbResult.BytesDownloaded,
                    ElapsedSeconds = lbResult.ElapsedSeconds,
                    SpeedMbps = lbResult.SpeedMbps,
                    Success = lbResult.Success,
                    ErrorMessage = lbResult.ErrorMessage
                });
            }

            // Save all results to database using a new DbContext (background task)
            await using var db = new DavDatabaseContext();
            db.ProviderBenchmarkResults.AddRange(dbResults);
            await db.SaveChangesAsync().ConfigureAwait(false);

            // Refresh the affinity service's benchmark cache so new results are used for provider selection
            await affinityService.RefreshBenchmarkSpeeds().ConfigureAwait(false);

            // Log summary
            var successfulResults = response.Results.Where(r => r.Success).ToList();
            var failedResults = response.Results.Where(r => !r.Success).ToList();

            Log.Information("[Benchmark] === Benchmark Complete ===");
            Log.Information("[Benchmark] Test file: {FileName}", fileName);
            Log.Information("[Benchmark] Successful: {Success}, Failed: {Failed}",
                successfulResults.Count, failedResults.Count);

            if (successfulResults.Count > 0)
            {
                var fastest = successfulResults.OrderByDescending(r => r.SpeedMbps).First();
                var slowest = successfulResults.OrderBy(r => r.SpeedMbps).First();
                Log.Information("[Benchmark] Fastest: {Host} @ {Speed:F2} MB/s", fastest.ProviderHost, fastest.SpeedMbps);
                Log.Information("[Benchmark] Slowest: {Host} @ {Speed:F2} MB/s", slowest.ProviderHost, slowest.SpeedMbps);
            }

            foreach (var failed in failedResults)
            {
                Log.Warning("[Benchmark] Failed: {Host} - {Error}", failed.ProviderHost, failed.ErrorMessage);
            }

            Log.Information("[Benchmark] Results saved to database (RunId: {RunId})", runId);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("[Benchmark] Background benchmark timed out (10 minute limit)");
            response.Status = false;
            response.Error = "Benchmark timed out after 10 minutes.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Benchmark] Background benchmark error: {Message}", ex.Message);
            response.Status = false;
            response.Error = ex.Message;
        }
        finally
        {
            // Send final completion message
            response.IsComplete = true;
            await SendBenchmarkProgress(response).ConfigureAwait(false);
            Log.Information("[Benchmark] Background task completed for RunId: {RunId}", runId);
        }
    }

    private async Task<IActionResult> GetBenchmarkHistory()
    {
        var response = new ProviderBenchmarkHistoryResponse { Status = true };

        try
        {
            // Get distinct runs, ordered by most recent first
            var runs = await dbContext.ProviderBenchmarkResults
                .GroupBy(r => r.RunId)
                .Select(g => new
                {
                    RunId = g.Key,
                    CreatedAt = g.First().CreatedAt,
                    TestFileName = g.First().TestFileName,
                    TestFileSize = g.First().TestFileSize,
                    TestSizeMb = g.First().TestSizeMb,
                    Results = g.ToList()
                })
                .OrderByDescending(r => r.CreatedAt)
                .Take(10) // Last 10 benchmark runs
                .ToListAsync()
                .ConfigureAwait(false);

            response.Runs = runs.Select(r => new ProviderBenchmarkRunSummary
            {
                RunId = r.RunId,
                CreatedAt = r.CreatedAt,
                TestFileName = r.TestFileName,
                TestFileSize = r.TestFileSize,
                TestSizeMb = r.TestSizeMb,
                Results = r.Results.Select(res => new ProviderBenchmarkResultDto
                {
                    ProviderIndex = res.ProviderIndex,
                    ProviderHost = res.ProviderHost,
                    ProviderType = res.ProviderType,
                    IsLoadBalanced = res.IsLoadBalanced,
                    BytesDownloaded = res.BytesDownloaded,
                    ElapsedSeconds = res.ElapsedSeconds,
                    SpeedMbps = res.SpeedMbps,
                    Success = res.Success,
                    ErrorMessage = res.ErrorMessage
                }).ToList()
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Benchmark] Error getting benchmark history");
            response.Status = false;
            response.Error = ex.Message;
        }

        return Ok(response);
    }

    /// <summary>
    /// Gets a file by its ID, trying all file types (NZB, Multipart, RAR).
    /// </summary>
    private async Task<(string FileName, string[] SegmentIds, long FileSize, Guid FileId, long[]? SegmentSizes)?> GetFileById(Guid id)
    {
        // Try NZB first
        var nzbResult = await GetNzbFileData(id).ConfigureAwait(false);
        if (nzbResult != null) return nzbResult;

        // Try Multipart
        var multipartResult = await GetMultipartFileData(id).ConfigureAwait(false);
        if (multipartResult != null) return multipartResult;

        // Try RAR
        var rarResult = await GetRarFileData(id).ConfigureAwait(false);
        if (rarResult != null) return rarResult;

        return null;
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize, Guid FileId, long[]? SegmentSizes)?> FindRandomLargeFile(HashSet<Guid>? excludeIds = null)
    {
        // Get IDs with missing article errors (these should be avoided if possible)
        var idsWithErrors = await dbContext.MissingArticleSummaries
            .Select(m => m.DavItemId)
            .Distinct()
            .ToListAsync()
            .ConfigureAwait(false);

        var idsWithErrorsSet = idsWithErrors.ToHashSet();
        excludeIds ??= [];

        // Try to find a file with no errors first, using a fast limited query
        // Query each type with LIMIT to avoid loading all files into memory

        // 1. Try NzbFiles first (most common type) - get a small batch of candidates
        var nzbCandidates = await dbContext.NzbFiles
            .Include(n => n.DavItem)
            .Where(n => n.DavItem != null &&
                        n.DavItem.FileSize >= MinFileSizeBytes &&
                        !n.DavItem.IsCorrupted)
            .Take(50) // Only check 50 candidates (randomization done client-side below)
            .Select(n => new { n.Id, n.DavItem!.Name, n.DavItem.FileSize, n.SegmentIds, Type = "Nzb" })
            .ToListAsync()
            .ConfigureAwait(false);

        // Filter for files with segments, exclude already-tried files, and prefer those without errors
        var validNzb = nzbCandidates
            .Where(f => f.SegmentIds.Length > 0 && !excludeIds.Contains(f.Id))
            .OrderBy(f => idsWithErrorsSet.Contains(f.Id) ? 1 : 0)
            .ThenBy(_ => Random.Shared.Next())
            .FirstOrDefault();

        if (validNzb != null && !idsWithErrorsSet.Contains(validNzb.Id))
        {
            Log.Information("[Benchmark] Selected NZB file: {Name} ({Size:F2} GB, 0 errors)",
                validNzb.Name, (validNzb.FileSize ?? 0) / 1024.0 / 1024.0 / 1024.0);
            return await GetNzbFileData(validNzb.Id).ConfigureAwait(false);
        }

        // 2. Try MultipartFiles
        var multipartCandidates = await dbContext.MultipartFiles
            .Include(m => m.DavItem)
            .Where(m => m.DavItem != null &&
                        m.DavItem.FileSize >= MinFileSizeBytes &&
                        !m.DavItem.IsCorrupted)
            .Take(50)
            .Select(m => new { m.Id, m.DavItem!.Name, m.DavItem.FileSize, Type = "Multipart" })
            .ToListAsync()
            .ConfigureAwait(false);

        var validMultipart = multipartCandidates
            .Where(f => !excludeIds.Contains(f.Id))
            .OrderBy(f => idsWithErrorsSet.Contains(f.Id) ? 1 : 0)
            .ThenBy(_ => Random.Shared.Next())
            .FirstOrDefault();

        if (validMultipart != null && !idsWithErrorsSet.Contains(validMultipart.Id))
        {
            Log.Information("[Benchmark] Selected Multipart file: {Name} ({Size:F2} GB, 0 errors)",
                validMultipart.Name, (validMultipart.FileSize ?? 0) / 1024.0 / 1024.0 / 1024.0);
            return await GetMultipartFileData(validMultipart.Id).ConfigureAwait(false);
        }

        // 3. Try RarFiles
        var rarCandidates = await dbContext.RarFiles
            .Include(r => r.DavItem)
            .Where(r => r.DavItem != null &&
                        r.DavItem.FileSize >= MinFileSizeBytes &&
                        !r.DavItem.IsCorrupted)
            .Take(50)
            .Select(r => new { r.Id, r.DavItem!.Name, r.DavItem.FileSize, Type = "Rar" })
            .ToListAsync()
            .ConfigureAwait(false);

        var validRar = rarCandidates
            .Where(f => !excludeIds.Contains(f.Id))
            .OrderBy(f => idsWithErrorsSet.Contains(f.Id) ? 1 : 0)
            .ThenBy(_ => Random.Shared.Next())
            .FirstOrDefault();

        if (validRar != null && !idsWithErrorsSet.Contains(validRar.Id))
        {
            Log.Information("[Benchmark] Selected RAR file: {Name} ({Size:F2} GB, 0 errors)",
                validRar.Name, (validRar.FileSize ?? 0) / 1024.0 / 1024.0 / 1024.0);
            return await GetRarFileData(validRar.Id).ConfigureAwait(false);
        }

        // 4. If no error-free files found, fall back to any valid file (preferring fewer errors)
        // Use the first valid file we found from any type
        if (validNzb != null)
        {
            Log.Information("[Benchmark] Selected NZB file (with errors): {Name} ({Size:F2} GB)",
                validNzb.Name, (validNzb.FileSize ?? 0) / 1024.0 / 1024.0 / 1024.0);
            return await GetNzbFileData(validNzb.Id).ConfigureAwait(false);
        }
        if (validMultipart != null)
        {
            Log.Information("[Benchmark] Selected Multipart file (with errors): {Name} ({Size:F2} GB)",
                validMultipart.Name, (validMultipart.FileSize ?? 0) / 1024.0 / 1024.0 / 1024.0);
            return await GetMultipartFileData(validMultipart.Id).ConfigureAwait(false);
        }
        if (validRar != null)
        {
            Log.Information("[Benchmark] Selected RAR file (with errors): {Name} ({Size:F2} GB)",
                validRar.Name, (validRar.FileSize ?? 0) / 1024.0 / 1024.0 / 1024.0);
            return await GetRarFileData(validRar.Id).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize, Guid FileId, long[]? SegmentSizes)?> GetNzbFileData(Guid id)
    {
        var nzbFile = await dbContext.NzbFiles
            .Include(n => n.DavItem)
            .FirstOrDefaultAsync(n => n.Id == id)
            .ConfigureAwait(false);

        if (nzbFile?.SegmentIds.Length > 0)
        {
            return (
                nzbFile.DavItem?.Name ?? "Unknown",
                nzbFile.SegmentIds,
                nzbFile.DavItem?.FileSize ?? 0,
                nzbFile.Id,
                nzbFile.GetSegmentSizes()
            );
        }
        return null;
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize, Guid FileId, long[]? SegmentSizes)?> GetMultipartFileData(Guid id)
    {
        var multipartFile = await dbContext.MultipartFiles
            .Include(m => m.DavItem)
            .FirstOrDefaultAsync(m => m.Id == id)
            .ConfigureAwait(false);

        if (multipartFile != null)
        {
            var segmentIds = multipartFile.Metadata?.FileParts?
                .SelectMany(p => p.SegmentIds)
                .ToArray() ?? [];

            if (segmentIds.Length > 0)
            {
                return (
                    multipartFile.DavItem?.Name ?? "Unknown",
                    segmentIds,
                    multipartFile.DavItem?.FileSize ?? 0,
                    multipartFile.Id,
                    null // Multipart files don't store segment sizes separately
                );
            }
        }
        return null;
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize, Guid FileId, long[]? SegmentSizes)?> GetRarFileData(Guid id)
    {
        var rarFile = await dbContext.RarFiles
            .Include(r => r.DavItem)
            .FirstOrDefaultAsync(r => r.Id == id)
            .ConfigureAwait(false);

        if (rarFile != null)
        {
            var segmentIds = rarFile.RarParts?
                .SelectMany(p => p.SegmentIds)
                .ToArray() ?? [];

            if (segmentIds.Length > 0)
            {
                return (
                    rarFile.DavItem?.Name ?? "Unknown",
                    segmentIds,
                    rarFile.DavItem?.FileSize ?? 0,
                    rarFile.Id,
                    null // RAR files don't store segment sizes separately
                );
            }
        }
        return null;
    }

    private async Task<ProviderBenchmarkResultDto> TestProvider(
        int providerIndex,
        string providerHost,
        string providerType,
        string[] segmentIds,
        long fileSize,
        string fileName,
        bool isLoadBalanced,
        long[]? segmentSizes,
        CancellationToken cancellationToken)
    {
        var result = new ProviderBenchmarkResultDto
        {
            ProviderIndex = providerIndex,
            ProviderHost = providerHost,
            ProviderType = providerType,
            IsLoadBalanced = isLoadBalanced
        };

        var targetSize = (long)TestSizeMb * 1024 * 1024;
        var affinityKey = FilenameNormalizer.NormalizeName(System.IO.Path.GetFileNameWithoutExtension(fileName));

        // Track progress outside try block so exception handlers can calculate partial speed
        var totalRead = 0L;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Streaming,
                new ConnectionUsageDetails
                {
                    Text = $"Benchmark: {fileName}",
                    JobName = fileName,
                    AffinityKey = affinityKey,
                    ForcedProviderIndex = isLoadBalanced ? null : providerIndex,
                    DisableGracefulDegradation = true // Throw exception on permanent failure instead of zero-filling
                }
            );

            await using var stream = usenetClient.GetFileStream(
                segmentIds,
                fileSize,
                ConnectionsPerTest,
                usageContext,
                useBufferedStreaming: true,
                bufferSize: ConnectionsPerTest * 5,
                segmentSizes: segmentSizes
            );

            var buffer = new byte[256 * 1024]; // 256KB chunks
            var lastLoggedMb = 0L;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // 5 minute timeout per provider

            while (totalRead < targetSize && !cts.Token.IsCancellationRequested)
            {
                var bytesToRead = (int)Math.Min(buffer.Length, targetSize - totalRead);
                var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cts.Token).ConfigureAwait(false);

                if (read == 0)
                {
                    Log.Warning("[Benchmark] Stream ended early at {Read:F1} MB (expected {Target} MB)",
                        totalRead / 1024.0 / 1024.0, targetSize / 1024.0 / 1024.0);
                    break;
                }
                totalRead += read;

                // Log progress every 100 MB
                var currentMb = totalRead / 1024 / 1024;
                if (currentMb >= lastLoggedMb + 100)
                {
                    var currentSpeed = (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds;
                    Log.Information("[Benchmark] {Host}: {Read} MB / {Target} MB ({Speed:F1} MB/s)",
                        providerHost, currentMb, TestSizeMb, currentSpeed);
                    lastLoggedMb = currentMb;
                }
            }

            stopwatch.Stop();

            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = totalRead > 0 ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds : 0;
            result.Success = true;

            Log.Information("[Benchmark] Download complete from {Host}: {Bytes:F1} MB in {Time:F1}s = {Speed:F2} MB/s",
                providerHost, totalRead / 1024.0 / 1024.0, result.ElapsedSeconds, result.SpeedMbps);
        }
        catch (PermanentSegmentFailureException ex)
        {
            // Permanent failure (article not found, etc.) - stop test early but record achieved speed
            stopwatch.Stop();
            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = totalRead > 0 && stopwatch.Elapsed.TotalSeconds > 0
                ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds
                : 0;
            result.Success = totalRead > 0; // Consider successful if we got some data
            result.ErrorMessage = $"Stopped early: {ex.FailureReason}";

            Log.Warning("[Benchmark] Provider {Host} test stopped early due to permanent failure at segment {Segment}: {Reason}. Achieved {Speed:F2} MB/s ({Bytes:F1} MB in {Time:F1}s)",
                providerHost, ex.SegmentIndex, ex.FailureReason, result.SpeedMbps, totalRead / 1024.0 / 1024.0, result.ElapsedSeconds);
        }
        catch (OperationCanceledException ex)
        {
            // Timeout - record partial speed if we got any data
            stopwatch.Stop();
            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = totalRead > 0 && stopwatch.Elapsed.TotalSeconds > 0
                ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds
                : 0;
            result.Success = totalRead > 0; // Consider successful if we got some data
            result.ErrorMessage = totalRead > 0
                ? $"Timed out after {totalRead / 1024.0 / 1024.0:F1} MB"
                : "Test timed out" + (ex.Message.Length > 0 ? $": {ex.Message}" : "");

            Log.Warning("[Benchmark] Provider {Host} test timed out. Achieved {Speed:F2} MB/s ({Bytes:F1} MB in {Time:F1}s)",
                providerHost, result.SpeedMbps, totalRead / 1024.0 / 1024.0, result.ElapsedSeconds);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("segment size is unknown"))
        {
            // First segment failed - provider likely doesn't have this article
            stopwatch.Stop();
            result.BytesDownloaded = 0;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = 0;
            result.Success = false;
            result.ErrorMessage = "First segment unavailable on this provider (article not found or missing)";

            Log.Warning("[Benchmark] Provider {Host} failed: First segment not available. Provider may not have this article.",
                providerHost);
        }
        catch (Exception ex)
        {
            // Other errors - record partial speed if we got any data
            stopwatch.Stop();
            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = totalRead > 0 && stopwatch.Elapsed.TotalSeconds > 0
                ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds
                : 0;
            result.Success = totalRead > 0; // Consider successful if we got some data
            result.ErrorMessage = string.IsNullOrEmpty(ex.Message)
                ? $"Error: {ex.GetType().Name}"
                : ex.Message;

            Log.Warning(ex, "[Benchmark] Provider {Host} test failed: {Message}. Achieved {Speed:F2} MB/s ({Bytes:F1} MB)",
                providerHost, result.ErrorMessage, result.SpeedMbps, totalRead / 1024.0 / 1024.0);
        }

        return result;
    }

    private async Task SendBenchmarkProgress(ProviderBenchmarkResponse response)
    {
        try
        {
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await websocketManager.SendMessage(WebsocketTopic.BenchmarkProgress, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug("[Benchmark] Failed to send WebSocket progress: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Check if the first segment of a file is available on ALL selected providers.
    /// Tests each provider individually to ensure the benchmark will work for all of them.
    /// </summary>
    private async Task<(bool AllAvailable, List<int> AvailableProviders, List<int> UnavailableProviders)> CheckFileAvailabilityOnProviders(
        string[] segmentIds,
        string fileName,
        List<(int Index, string Host, string Type, int MaxConnections)> providers,
        CancellationToken ct)
    {
        if (segmentIds.Length == 0)
            return (false, [], providers.Select(p => p.Index).ToList());

        var firstSegmentId = segmentIds[0];
        var availableProviders = new List<int>();
        var unavailableProviders = new List<int>();

        Log.Information("[Benchmark] Checking availability of {FileName} on {Count} providers...", fileName, providers.Count);

        // Test each provider in parallel for speed
        var tasks = providers.Select(async provider =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15)); // 15 second timeout per provider

                var usageContext = new ConnectionUsageContext(
                    ConnectionUsageType.Streaming,
                    new ConnectionUsageDetails
                    {
                        Text = $"Benchmark availability check: {fileName}",
                        JobName = fileName,
                        ForcedProviderIndex = provider.Index,
                        DisableGracefulDegradation = true
                    }
                );

                await using var stream = usenetClient.GetFileStream(
                    [firstSegmentId],
                    500 * 1024, // Assume ~500KB for first segment
                    1,
                    usageContext,
                    useBufferedStreaming: false
                );

                var buffer = new byte[1024];
                var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);

                return (provider.Index, provider.Host, Available: read > 0);
            }
            catch (Exception ex)
            {
                Log.Debug("[Benchmark] Provider {Host} availability check failed: {Error}", provider.Host, ex.Message);
                return (provider.Index, provider.Host, Available: false);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (result.Available)
            {
                availableProviders.Add(result.Index);
                Log.Debug("[Benchmark] Provider {Host}: first segment available", result.Host);
            }
            else
            {
                unavailableProviders.Add(result.Index);
                Log.Debug("[Benchmark] Provider {Host}: first segment NOT available", result.Host);
            }
        }

        var allAvailable = unavailableProviders.Count == 0;
        Log.Information("[Benchmark] Availability check for {FileName}: {Available}/{Total} providers have the file",
            fileName, availableProviders.Count, providers.Count);

        return (allAvailable, availableProviders, unavailableProviders);
    }
}
