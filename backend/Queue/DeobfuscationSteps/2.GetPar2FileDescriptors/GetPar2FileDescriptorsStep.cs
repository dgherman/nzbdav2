using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

namespace NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;

public static class GetPar2FileDescriptorsStep
{
    /// <summary>
    /// Number of connections to use for Par2 file streaming.
    /// Using multiple connections significantly speeds up Par2 extraction for large files.
    /// </summary>
    private const int Par2StreamConnections = 15;

    /// <summary>
    /// Buffer size multiplier for Par2 streaming.
    /// Larger buffers reduce stalls when reading Par2 packets.
    /// </summary>
    private const int Par2BufferMultiplier = 5;

    public static async Task<List<FileDesc>> GetPar2FileDescriptors
    (
        List<FetchFirstSegmentsStep.NzbFileWithFirstSegment> files,
        UsenetStreamingClient client,
        CancellationToken cancellationToken = default
    )
    {
        // find the par2 index file
        var par2Index = files
            .Where(x => !x.MissingFirstSegment)
            .Where(x => Par2.HasPar2MagicBytes(x.First16KB!))
            .MinBy(x => x.NzbFile.Segments.Count);
        if (par2Index is null) return [];

        // Calculate expected number of file descriptors for early termination
        // This is the count of non-Par2 files (RAR parts, video files, etc.)
        var expectedDescriptors = files.Count(f =>
            !f.MissingFirstSegment &&
            !Par2.HasPar2MagicBytes(f.First16KB!) &&
            !f.NzbFile.FileName.EndsWith(".par2", StringComparison.OrdinalIgnoreCase));

        // Create a timeout cancellation token (3 minutes to allow for slow providers)
        // Increased from 2 minutes to reduce false positives while still catching hung operations
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
        var timeoutToken = timeoutCts.Token;
        var startTime = DateTimeOffset.UtcNow;

        // Add periodic logging to detect long-running Par2 extraction
        var loggingCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), loggingCts.Token).ConfigureAwait(false);
                if (!loggingCts.Token.IsCancellationRequested)
                {
                    Serilog.Log.Warning("[GetPar2] Par2 extraction is taking longer than 30 seconds for {FileName}. Still processing...",
                        par2Index.NzbFile.FileName);
                }
                await Task.Delay(TimeSpan.FromSeconds(60), loggingCts.Token).ConfigureAwait(false);
                if (!loggingCts.Token.IsCancellationRequested)
                {
                    Serilog.Log.Warning("[GetPar2] Par2 extraction is taking longer than 90 seconds for {FileName}. Still processing...",
                        par2Index.NzbFile.FileName);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when operation completes or times out
            }
        }, loggingCts.Token);

        try
        {
            // return all file descriptors
            var fileDescriptors = new List<FileDesc>();
            var segments = par2Index.NzbFile.GetSegmentIds();
            var filesize = par2Index.NzbFile.Segments.Count == 1
                ? par2Index.Header!.PartOffset + par2Index.Header!.PartSize
                : await client.GetFileSizeAsync(par2Index.NzbFile, timeoutToken).ConfigureAwait(false);

            // Create Analysis usage context for Par2 segment fetches to avoid consuming Queue permits
            // Par2 extraction already holds one Queue permit - segment fetches should use Analysis pool
            var originalContext = cancellationToken.GetContext<ConnectionUsageContext>();
            var par2Context = new ConnectionUsageContext(
                ConnectionUsageType.Analysis,
                originalContext.DetailsObject ?? new ConnectionUsageDetails { Text = originalContext.Details ?? "" }
            );
            using var _ = timeoutToken.SetScopedContext(par2Context);

            // Use early termination: stop reading once we've found all expected file descriptors
            // This avoids reading recovery blocks which can be very large (gigabytes of data)
            var maxDescriptors = expectedDescriptors > 0 ? expectedDescriptors : (int?)null;

            // For small Par2 files (1-3 segments), download entirely to memory first then parse
            // This is much faster than buffered streaming for small files due to reduced overhead
            const int SmallFileSegmentThreshold = 3;
            if (segments.Length <= SmallFileSegmentThreshold)
            {
                Serilog.Log.Debug("[GetPar2] Using fast memory-based extraction for small Par2 file ({Segments} segments, {Size} bytes)",
                    segments.Length, filesize);

                // Download the entire file to memory
                await using var downloadStream = client.GetFileStream(
                    segments,
                    filesize,
                    Math.Min(Par2StreamConnections, segments.Length),
                    par2Context,
                    useBufferedStreaming: true,
                    bufferSize: segments.Length * 3
                );

                using var memoryStream = new MemoryStream((int)filesize);
                await downloadStream.CopyToAsync(memoryStream, timeoutToken).ConfigureAwait(false);
                memoryStream.Position = 0;

                Serilog.Log.Debug("[GetPar2] Downloaded {Bytes} bytes to memory in {Elapsed:F2}s. Now parsing...",
                    memoryStream.Length, (DateTimeOffset.UtcNow - startTime).TotalSeconds);

                // Parse from memory (very fast, no network I/O during parsing)
                await foreach (var fileDescriptor in Par2.ReadFileDescriptions(memoryStream, timeoutToken, maxDescriptors).ConfigureAwait(false))
                    fileDescriptors.Add(fileDescriptor);
            }
            else
            {
                // For larger Par2 files, use streaming to avoid downloading gigabytes of recovery data
                var connectionCount = Math.Min(Par2StreamConnections, segments.Length);
                var bufferSize = connectionCount * Par2BufferMultiplier;

                Serilog.Log.Debug("[GetPar2] Starting Par2 streaming extraction with {Connections} connections, buffer {Buffer} for {Segments} segments. Expected descriptors: {Expected}",
                    connectionCount, bufferSize, segments.Length, expectedDescriptors);

                await using var stream = client.GetFileStream(
                    segments,
                    filesize,
                    connectionCount,
                    par2Context,
                    useBufferedStreaming: true,
                    bufferSize: bufferSize
                );

                await foreach (var fileDescriptor in Par2.ReadFileDescriptions(stream, timeoutToken, maxDescriptors).ConfigureAwait(false))
                    fileDescriptors.Add(fileDescriptor);
            }
            
            Serilog.Log.Information("[GetPar2] Found {Count} file descriptors in {FileName}. Elapsed: {Elapsed:F2}s",
                fileDescriptors.Count, par2Index.NzbFile.FileName, (DateTimeOffset.UtcNow - startTime).TotalSeconds);

            // Cancel logging task since we completed successfully
            loggingCts.Cancel();
            loggingCts.Dispose();

            return fileDescriptors;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            loggingCts.Dispose();
            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            throw new TimeoutException($"Par2 file descriptor extraction timed out after {elapsed:F1} seconds (limit: 3 minutes) for file: {par2Index.NzbFile.FileName}");
        }
        catch
        {
            loggingCts.Dispose();
            throw;
        }
    }
}