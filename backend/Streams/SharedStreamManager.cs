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

    // Stream rather than BufferedSegmentStream: the entry only needs Read/Dispose, and the looser
    // type lets the manager be exercised with an in-memory stream in tests.
    public readonly record struct SharedStreamFactoryResult(Stream Stream, IDisposable? ContextScope = null);

    /// <summary>
    /// Try to attach to an existing shared stream for this file.
    /// Returns a handle if the entry exists and the position is within range, null otherwise.
    /// </summary>
    /// <remarks>
    /// This is the ONLY site that records hits and misses, so hits + misses == attach attempts.
    /// <see cref="GetOrCreate"/> deliberately records nothing under those counters even though it
    /// re-attempts the same attach: doing so previously filed one failed attach twice, once as
    /// "position_out_of_range" here and once as "existing_entry_unattachable" there, which made a
    /// single failure mode read as two independent ones. See issue #18.
    /// </remarks>
    public static SharedStreamHandle? TryAttach(Guid davItemId, long startPosition)
    {
        if (!s_entries.TryGetValue(davItemId, out var entry))
        {
            AppMetrics.SharedStreamMisses.WithLabels("no_entry").Inc();
            return null;
        }

        var handle = entry.TryAttachReader(startPosition, out var rejection, out var distance);
        if (handle != null)
        {
            AppMetrics.SharedStreamHits.Inc();
            Log.Debug("[SharedStreamManager] Attached to existing shared stream. DavItemId={DavItemId}, Position={Position}", davItemId, startPosition);
            return handle;
        }

        RecordAttachMiss(davItemId, startPosition, rejection, distance);
        return null;
    }

    /// <summary>
    /// Files a failed attach under its specific reason and records how far from the write frontier
    /// the reader was. The distance is the number issue #18 turns on: rejections clustered within a
    /// ring's width mean a larger ring recovers them, while rejections in the GB range mean the file
    /// needs more than one pumped entry and no ring size will help.
    /// </summary>
    private static void RecordAttachMiss(Guid davItemId, long startPosition, SharedStreamEntry.AttachRejection rejection, long distance)
    {
        var reason = rejection switch
        {
            SharedStreamEntry.AttachRejection.EntryUnusable => "entry_unusable",
            SharedStreamEntry.AttachRejection.BeforeBase => "before_base",
            SharedStreamEntry.AttachRejection.BehindWindow => "behind_window",
            SharedStreamEntry.AttachRejection.AheadOfFrontier => "ahead_of_frontier",
            SharedStreamEntry.AttachRejection.PastEnd => "past_end",
            _ => "unknown",
        };
        AppMetrics.SharedStreamMisses.WithLabels(reason).Inc();

        // EntryUnusable is a lifecycle race, not a positioning problem — its distance would be noise.
        if (rejection != SharedStreamEntry.AttachRejection.EntryUnusable)
        {
            AppMetrics.SharedStreamAttachMissDistanceBytes
                .WithLabels(distance >= 0 ? "ahead" : "behind")
                .Observe(Math.Abs(distance));
        }

        Log.Debug("[SharedStreamManager] Attach refused. DavItemId={DavItemId}, Position={Position}, Reason={Reason}, DistanceFromFrontier={Distance}",
            davItemId, startPosition, reason, distance);
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
                // Not counted as a hit: TryAttach already filed this request's outcome, and the
                // only way to reach here with a live entry is an entry appearing in the gap between
                // the two calls. Recorded on the create counter instead.
                AppMetrics.SharedStreamCreates.WithLabels("raced_attached").Inc();
                Log.Debug("[SharedStreamManager] Attached to existing entry (race). DavItemId={DavItemId}", davItemId);
                return handle;
            }

            // Entry exists but this position can't attach — normally a player seeking to the mkv
            // tail/Cues, far ahead of the front pump. Give up now: the caller falls back to a private
            // BufferedSegmentStream at the seek target, which is exactly what serves that read.
            // Building an entry here instead would start a full fetch pipeline (permits, connections,
            // real segment reads) only to lose the TryAdd against the entry we just found and be torn
            // down synchronously — a wasted pipeline on the click-to-play path, on every file open.
            AppMetrics.SharedStreamCreates.WithLabels("skipped_entry_unattachable").Inc();
            Log.Debug("[SharedStreamManager] Entry exists but position {Position} cannot attach; caller will use a private stream. DavItemId={DavItemId}",
                startPosition, davItemId);
            return null;
        }

        // Acquire a semaphore slot
        var slot = BufferedSegmentStream.TryAcquireSlot();
        if (slot == null)
        {
            AppMetrics.SharedStreamCreates.WithLabels("no_slot").Inc();
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

            // Try to add — if another thread raced us, clean up our entry and attach to theirs.
            // Only a genuine concurrent create can land here now: an already-registered entry is
            // handled by the fast path above, which never reaches the factory.
            if (!s_entries.TryAdd(davItemId, entry))
            {
                // Don't call Dispose — that would evict the winner's entry from the dictionary!
                entry.DisposeWithoutEvict();
                // Try the winner's entry
                if (s_entries.TryGetValue(davItemId, out var winner))
                {
                    var winnerHandle = winner.TryAttachReader(startPosition);
                    AppMetrics.SharedStreamCreates
                        .WithLabels(winnerHandle != null ? "lost_race_attached" : "lost_race_unattachable").Inc();
                    return winnerHandle;
                }
                AppMetrics.SharedStreamCreates.WithLabels("lost_race_winner_gone").Inc();
                return null;
            }

            // Register the first reader's position for backpressure tracking
            var handleId = entry.RegisterReader(startPosition);
            entry.StartPump();
            AppMetrics.SharedStreamActiveEntries.Set(s_entries.Count);
            AppMetrics.SharedStreamCreates.WithLabels("created").Inc();
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

    /// <summary>
    /// Refresh shared-stream gauges. Called periodically by PoolMetricsCollector;
    /// reader counts change on every attach/detach so they are sampled rather than
    /// updated inline at each site.
    /// </summary>
    public static void RefreshGauges()
    {
        AppMetrics.SharedStreamActiveEntries.Set(s_entries.Count);
        AppMetrics.SharedStreamActiveReaders.Set(s_entries.Values.Sum(e => e.ActiveReaders));
    }
}
