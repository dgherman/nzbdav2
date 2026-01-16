using System.Threading.Channels;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Service that tracks provider usage by queuing events and batch-inserting them into the database
/// </summary>
public class ProviderUsageTrackingService
{
    private readonly Channel<ProviderUsageEvent> _eventQueue;
    private readonly CancellationToken _cancellationToken = SigtermUtil.GetCancellationToken();

    public ProviderUsageTrackingService()
    {
        _eventQueue = Channel.CreateUnbounded<ProviderUsageEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _ = StartProcessingQueue();
    }

    /// <summary>
    /// Queues a provider usage event for async database insertion
    /// </summary>
    public void TrackProviderUsage(
        string providerHost,
        string providerType,
        string? operationType = null,
        long? bytesTransferred = null)
    {
        var evt = new ProviderUsageEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderHost = providerHost,
            ProviderType = providerType,
            OperationType = operationType,
            BytesTransferred = bytesTransferred
        };

        // Try to write to channel without blocking
        if (!_eventQueue.Writer.TryWrite(evt))
        {
            Log.Warning("Failed to queue provider usage event for {ProviderHost}", providerHost);
        }
    }

    private async Task StartProcessingQueue()
    {
        var batchSize = 100;
        var batchTimeout = TimeSpan.FromSeconds(5);

        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                var batch = new List<ProviderUsageEvent>();

                // Read from channel with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
                cts.CancelAfter(batchTimeout);

                try
                {
                    // Collect up to batchSize events or until timeout
                    while (batch.Count < batchSize &&
                           await _eventQueue.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                    {
                        if (_eventQueue.Reader.TryRead(out var evt))
                        {
                            batch.Add(evt);
                        }
                    }
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !_cancellationToken.IsCancellationRequested)
                {
                    // Timeout reached, process what we have
                }

                // Insert batch if we have any events
                if (batch.Count > 0)
                {
                    await InsertBatch(batch).ConfigureAwait(false);
                }
                else
                {
                    // No events, wait a bit before checking again
                    await Task.Delay(TimeSpan.FromSeconds(1), _cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!_cancellationToken.IsCancellationRequested)
            {
                Log.Error(ex, "Error processing provider usage event queue");
                await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task InsertBatch(List<ProviderUsageEvent> events)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            await dbContext.ProviderUsageEvents.AddRangeAsync(events, _cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(_cancellationToken).ConfigureAwait(false);

            Log.Debug("Inserted {Count} provider usage events into database", events.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to insert provider usage events batch");
        }
    }
}
