// ReSharper disable InconsistentNaming

using System.Threading;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using Usenet.Nzb;
using UsenetSharp.Models;
using Serilog;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

public static class FetchFirstSegmentsStep
{
    public static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegments
    (
        List<NzbFile> nzbFiles,
        UsenetStreamingClient client,
        ConfigManager configManager,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null
    )
    {
        var logger = new ComponentLogger(LogComponents.Queue, configManager);
        var totalFiles = nzbFiles.Count(x => x.Segments.Count > 0);
        var maxConcurrency = configManager.GetMaxQueueConnections();
        var timeoutSeconds = configManager.GetUsenetOperationTimeout();

        logger.Information("Starting fetch for {TotalFiles} files (concurrency: {Concurrency}, timeout: {Timeout}s)",
            totalFiles, maxConcurrency, timeoutSeconds);

        var startTime = DateTimeOffset.UtcNow;
        var completed = 0;
        var failed = 0;
        
        // Track critical failures to ensure they aren't masked by cancellations
        var criticalFailure = (Exception?)null;

        var result = await nzbFiles
            .Where(x => x.Segments.Count > 0)
            .Select(async x => {
                var fileStart = DateTimeOffset.UtcNow;
                try
                {
                    // If we already hit a critical failure (like Missing Articles), don't start new work
                    if (criticalFailure != null) 
                    {
                        // Throw OperationCanceledException to skip this item gracefully in the pipeline
                        throw new OperationCanceledException("Skipping due to critical failure in another task");
                    }

                    var fileResult = await FetchFirstSegment(x, client, configManager, cancellationToken).ConfigureAwait(false);
                    var duration = (DateTimeOffset.UtcNow - fileStart).TotalSeconds;
                    
                    var current = Interlocked.Increment(ref completed);
                    if (current % 10 == 0 || current == totalFiles)
                    {
                        logger.Information("Progress: {Completed}/{Total} files ({Failed} failed)",
                            current, totalFiles, failed);
                    }
                    return new { Result = fileResult, Duration = duration, Name = x.FileName };
                }
                catch (UsenetArticleNotFoundException ex)
                {
                    // Prioritize missing articles as the critical failure reason
                    Interlocked.CompareExchange(ref criticalFailure, ex, null);
                    Interlocked.Increment(ref failed);
                    logger.Warning("Failed to fetch first segment for {FileName}: {Error}", x.FileName, ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.Warning("Failed to fetch first segment for {FileName}: {Error}", x.FileName, ex.Message);
                    throw;
                }
            })
            .WithConcurrencyAsync(maxConcurrency)
            .GetAllAsync(cancellationToken, progress).ConfigureAwait(false);

        // If we had a critical failure, rethrow it now to ensure it bubbles up correctly
        if (criticalFailure != null)
        {
            logger.Error("Rethrowing critical failure: {Message}", criticalFailure.Message);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(criticalFailure).Throw();
        }

        var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
        logger.Information("Completed {Completed}/{Total} files in {Elapsed:F1}s ({Failed} failed)",
            completed, totalFiles, elapsed, failed);

        // Log the slowest files to help identify bottlenecks
        var slowFiles = result
            .OrderByDescending(x => x.Duration)
            .Take(5)
            .ToList();

        if (slowFiles.Any(x => x.Duration > 5)) // only log if actually slow (>5s)
        {
            logger.Information("Top 5 slowest files during probe:");
            foreach(var f in slowFiles)
            {
                logger.Information("  - {FileName}: {Duration:F1}s", f.Name, f.Duration);
            }
        }

        return result.Select(r => r.Result).ToList();
    }

    private static async Task<NzbFileWithFirstSegment> FetchFirstSegment
    (
        NzbFile nzbFile,
        UsenetStreamingClient client,
        ConfigManager configManager,
        CancellationToken cancellationToken = default
    )
    {
        var logger = new ComponentLogger(LogComponents.Queue, configManager);
        var startTime = DateTimeOffset.UtcNow;
        logger.Debug("Starting: {FileName}", nzbFile.FileName);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Use configurable timeout (default: 180s for slower providers or large NZBs)
            var timeoutSeconds = configManager.GetUsenetOperationTimeout();
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var context = cancellationToken.GetContext<ConnectionUsageContext>();
            using var _ = timeoutCts.Token.SetScopedContext(context);

            // get the first article stream
            var firstSegment = nzbFile.Segments[0].MessageId.Value;
            logger.Debug("Getting stream for {FileName} (segment: {SegmentId})",
                nzbFile.FileName, firstSegment);

            await using var stream = await client.GetSegmentStreamAsync(firstSegment, true, timeoutCts.Token).ConfigureAwait(false);
            logger.Debug("Got stream for {FileName}, reading 16KB...", nzbFile.FileName);

            // read up to the first 16KB from the stream
            var totalRead = 0;
            var buffer = new byte[16 * 1024];
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    timeoutCts.Token).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            // determine bytes read
            var first16KB = totalRead < buffer.Length
                ? buffer.AsSpan(0, totalRead).ToArray()
                : buffer;

            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            logger.Debug("Completed {FileName} in {Elapsed:F2}s (read {Bytes} bytes)",
                nzbFile.FileName, elapsed, totalRead);

            // return
            return new NzbFileWithFirstSegment
            {
                NzbFile = nzbFile,
                First16KB = first16KB,
                Header = stream.Header,
                MissingFirstSegment = false,
                ReleaseDate = stream.ArticleHeaders!.Date
            };
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            logger.Warning("Cancelled {FileName} after {Elapsed:F1}s (parent cancellation)",
                nzbFile.FileName, elapsed);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            var timeoutSeconds = configManager.GetUsenetOperationTimeout();
            logger.Warning("Timed out {FileName} after {Elapsed:F1}s (timeout: {Timeout}s)",
                nzbFile.FileName, elapsed, timeoutSeconds);
            throw;
        }
        catch (UsenetArticleNotFoundException)
        {
            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            logger.Debug("Missing first segment for {FileName} (took {Elapsed:F2}s)",
                nzbFile.FileName, elapsed);

            // Fail fast for important files (RARs, Videos, etc.) as we cannot stream them without headers.
            if (FilenameUtil.IsImportantFileType(nzbFile.FileName))
            {
                Log.Error($"[FetchFirst] Critical file {nzbFile.FileName} is missing first segment. Failing job.");
                throw;
            }

            return new NzbFileWithFirstSegment
            {
                NzbFile = nzbFile,
                First16KB = null,
                Header = null,
                MissingFirstSegment = true,
                ReleaseDate = DateTimeOffset.UtcNow
            };
        }
    }

    public class NzbFileWithFirstSegment
    {
        private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static readonly byte[] Rar5Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
        private static readonly byte[] SevenZipMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

        public required NzbFile NzbFile { get; init; }
        public required UsenetYencHeader? Header { get; init; }
        public required byte[]? First16KB { get; init; }
        public required bool MissingFirstSegment { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public int MagicOffset { get; private set; } = -1;

        public bool HasRar4Magic() 
        {
            var offset = FindMagic(Rar4Magic);
            if (offset >= 0) MagicOffset = offset;
            return offset >= 0;
        }

        public bool HasRar5Magic()
        {
            var offset = FindMagic(Rar5Magic);
            if (offset >= 0) MagicOffset = offset;
            return offset >= 0;
        }

        public bool HasSevenZipMagic()
        {
            var offset = FindMagic(SevenZipMagic);
            if (offset >= 0) MagicOffset = offset;
            return offset >= 0;
        }

        private int FindMagic(byte[] sequence)
        {
            if (First16KB == null || First16KB.Length < sequence.Length) return -1;
            
            // Search for magic bytes in the first 16KB (most obfuscation prefixes are small)
            for (var i = 0; i <= First16KB.Length - sequence.Length; i++)
            {
                if (First16KB.AsSpan(i, sequence.Length).SequenceEqual(sequence))
                {
                    return i;
                }
            }
            return -1;
        }

        private bool HasMagic(byte[] sequence)
        {
            return FindMagic(sequence) == 0;
        }
    }
}