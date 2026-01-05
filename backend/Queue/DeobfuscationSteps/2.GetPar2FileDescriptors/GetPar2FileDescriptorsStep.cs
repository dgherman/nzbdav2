using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

namespace NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;

public static class GetPar2FileDescriptorsStep
{
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

            await using var stream = client.GetFileStream(segments, filesize, 1, par2Context);
            await foreach (var fileDescriptor in Par2.ReadFileDescriptions(stream, timeoutToken).ConfigureAwait(false))
                fileDescriptors.Add(fileDescriptor);
            
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