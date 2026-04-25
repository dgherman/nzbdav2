using System.Collections.Concurrent;
using NzbWebDAV.Metrics;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Static manager for shared stream entries. Keyed by DavItemId (Guid).
/// Multiple HTTP requests for the same file share a single BufferedSegmentStream
/// through this manager.
/// </summary>
public static class SharedStreamManager
{
    private static readonly ConcurrentDictionary<Guid, SharedStreamEntry> s_entries = new();

    public readonly record struct SharedStreamFactoryResult(BufferedSegmentStream Stream, IDisposable? ContextScope = null);

    /// <summary>
    /// Try to attach to an existing shared stream for this file.
    /// Returns a handle if the entry exists and the position is within range, null otherwise.
    /// </summary>
    public static SharedStreamHandle? TryAttach(Guid davItemId, long startPosition)
    {
        if (!s_entries.TryGetValue(davItemId, out var entry))
        {
            AppMetrics.SharedStreamMisses.WithLabels("", "no_entry").Inc();
            return null;
        }

        var handle = entry.TryAttachReader(startPosition);
        if (handle != null)
        {
            AppMetrics.SharedStreamHits.WithLabels("").Inc();
            Log.Debug("[SharedStreamManager] Attached to existing shared stream. DavItemId={DavItemId}, Position={Position}", davItemId, startPosition);
        }
        else
        {
            AppMetrics.SharedStreamMisses.WithLabels("", "position_out_of_range").Inc();
        }
        return handle;
    }

    /// <summary>
    /// Get or create a shared stream entry for this file.
    /// The factory is called only if no entry exists and a semaphore slot is acquired.
    /// Returns a handle, or null if no slot is available.
    /// </summary>
    /// <param name="davItemId">The file identifier</param>
    /// <param name="startPosition">The byte offset this reader starts at</param>
    /// <param name="streamLength">Total length of the stream</param>
    /// <param name="ringBufferSize">Ring buffer size in bytes</param>
    /// <param name="gracePeriodSeconds">Grace period before disposing after last reader detaches</param>
    /// <param name="factory">Creates a BufferedSegmentStream using the entry-scoped CancellationToken.
    /// Only called if creating a new entry. Must NOT call SetAcquiredSlot — the entry manages the semaphore slot.
    /// Must use the provided CancellationToken (not a request-scoped one) so the pump outlives individual requests.
    /// If a CancellationToken context is attached to the entry token, return its scope so the entry owns that lifetime.</param>
    public static SharedStreamHandle? GetOrCreate(
        Guid davItemId,
        long startPosition,
        long streamLength,
        int ringBufferSize,
        int gracePeriodSeconds,
        Func<CancellationToken, SharedStreamFactoryResult> factory)
    {
        // Fast path: entry already exists
        if (s_entries.TryGetValue(davItemId, out var existing))
        {
            var handle = existing.TryAttachReader(startPosition);
            if (handle != null)
            {
                AppMetrics.SharedStreamHits.WithLabels("").Inc();
                Log.Debug("[SharedStreamManager] Attached to existing entry (race). DavItemId={DavItemId}", davItemId);
                return handle;
            }
            // Entry exists but can't attach (position out of range, or entry is dying)
            // Fall through to try creating a new one — the old one will evict itself
            AppMetrics.SharedStreamMisses.WithLabels("", "existing_entry_unattachable").Inc();
        }
        else
        {
            AppMetrics.SharedStreamMisses.WithLabels("", "no_entry").Inc();
        }

        // Acquire a semaphore slot
        var slot = BufferedSegmentStream.TryAcquireSlot();
        if (slot == null)
        {
            Log.Debug("[SharedStreamManager] No semaphore slot available. DavItemId={DavItemId}", davItemId);
            return null;
        }

        try
        {
            // Create an entry-scoped CTS so the pump's inner stream survives
            // individual request cancellations — only cancelled when the entry itself is disposed
            var entryCts = new CancellationTokenSource();
            var factoryResult = factory(entryCts.Token);
            var innerStream = factoryResult.Stream;

            var entry = new SharedStreamEntry(
                innerStream,
                slot,
                davItemId,
                startPosition,
                streamLength,
                ringBufferSize,
                gracePeriodSeconds,
                Evict,
                entryCts,
                factoryResult.ContextScope
            );

            // Try to add — if another thread raced us, clean up our entry and attach to theirs
            if (!s_entries.TryAdd(davItemId, entry))
            {
                // Don't call Dispose — that would evict the winner's entry from the dictionary!
                entry.DisposeWithoutEvict();
                // Try the winner's entry
                if (s_entries.TryGetValue(davItemId, out var winner))
                {
                    return winner.TryAttachReader(startPosition);
                }
                return null;
            }

            // Register the first reader's position for backpressure tracking
            var handleId = entry.RegisterReader(startPosition);
            entry.StartPump();
            AppMetrics.SharedStreamActiveEntries.Set(s_entries.Count);
            Log.Information("[SharedStreamManager] Created shared stream entry. DavItemId={DavItemId}, Position={Position}, BufferSize={BufferSize}MB",
                davItemId, startPosition, ringBufferSize / (1024 * 1024));

            // The entry was created with readerCount=1, return the first handle
            return new SharedStreamHandle(entry, startPosition, handleId);
        }
        catch
        {
            slot.Release();
            throw;
        }
    }

    /// <summary>
    /// Remove an entry from the manager. Called by SharedStreamEntry on grace period expiry or failure.
    /// </summary>
    public static void Evict(Guid davItemId)
    {
        if (s_entries.TryRemove(davItemId, out _))
        {
            AppMetrics.SharedStreamActiveEntries.Set(s_entries.Count);
            Log.Debug("[SharedStreamManager] Evicted entry. DavItemId={DavItemId}", davItemId);
        }
    }

    /// <summary>
    /// Get the number of active entries (for diagnostics/debugging).
    /// </summary>
    public static int ActiveEntryCount => s_entries.Count;
}
