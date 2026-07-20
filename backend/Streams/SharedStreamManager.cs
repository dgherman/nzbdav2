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
    // A file holds SEVERAL entries, each pumping a different region of it.
    //
    // Keyed by DavItemId alone this was one entry per file, anchored wherever its first reader
    // landed — always byte 0, the header read. Production measurement on v0.11.10 (issue #18):
    // every attach refusal was "ahead_of_frontier" at a mean of 3.85 GB, none closer than 1 GB,
    // while the entry's pump had reached only 28-64 MB before starving and grace-expiring at ~14s.
    // Playback lives GB downstream of the header, so a single front-anchored entry can never serve
    // it, and no ring size closes a 3.85 GB gap. Two movies produced 11 private streams.
    //
    // The list is scanned linearly on attach. It is capped at a handful of entries, so a scan is
    // cheaper than the indexing that would replace it. Deliberately NOT keyed by a fixed region
    // bucket: an entry's pump advances, so the region it covers changes over its life and any
    // position-derived key would go stale the moment it started moving.
    private static readonly ConcurrentDictionary<Guid, List<SharedStreamEntry>> s_entries = new();

    /// <summary>
    /// Maximum concurrently pumped regions per file. Each costs a ring buffer plus one of the
    /// scarce global stream slots, so this bounds how much one file can take of both.
    /// Set to 1 to restore the pre-v0.11.11 single-entry behaviour.
    /// </summary>
    internal static int MaxEntriesPerFile =
        int.TryParse(Environment.GetEnvironmentVariable("NZBDAV_MAX_SHARED_ENTRIES_PER_FILE"), out var v) && v > 0
            ? v
            : 3;

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
        var entries = Snapshot(davItemId);
        if (entries.Length == 0)
        {
            AppMetrics.SharedStreamMisses.WithLabels("no_entry").Inc();
            return null;
        }

        // Take the first entry that accepts this position. Entries cover disjoint-ish regions, so
        // at most one can normally accept, and the scan is over a list capped at MaxEntriesPerFile.
        var nearestRejection = SharedStreamEntry.AttachRejection.EntryUnusable;
        var nearestDistance = long.MaxValue;

        foreach (var entry in entries)
        {
            var handle = entry.TryAttachReader(startPosition, out var rejection, out var distance);
            if (handle != null)
            {
                AppMetrics.SharedStreamHits.Inc();
                Log.Debug("[SharedStreamManager] Attached to existing shared stream. DavItemId={DavItemId}, Position={Position}", davItemId, startPosition);
                return handle;
            }

            // Report the miss against the CLOSEST entry, not an arbitrary one: the useful question
            // is how far this reader was from the nearest pump, not from whichever happened to be
            // first in the list.
            if (Math.Abs(distance) < Math.Abs(nearestDistance))
            {
                nearestDistance = distance;
                nearestRejection = rejection;
            }
        }

        RecordAttachMiss(davItemId, startPosition, nearestRejection, nearestDistance);
        return null;
    }

    /// <summary>
    /// Point-in-time copy of a file's entries. Taken under the list lock so a concurrent create or
    /// evict cannot mutate the list mid-scan; attaching is then done outside the lock, because
    /// TryAttachReader takes the entry's own lock and holding both would invert the lock order
    /// against Evict (which runs from an entry's teardown while that entry's lock is held).
    /// </summary>
    private static SharedStreamEntry[] Snapshot(Guid davItemId)
    {
        if (!s_entries.TryGetValue(davItemId, out var list)) return Array.Empty<SharedStreamEntry>();
        lock (list) return list.ToArray();
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
        // Fast path: an entry that appeared since the caller's TryAttach.
        foreach (var existing in Snapshot(davItemId))
        {
            var handle = existing.TryAttachReader(startPosition);
            if (handle == null) continue;

            // Not counted as a hit: TryAttach already filed this request's outcome, and the only
            // way to reach here is an entry becoming attachable in the gap between the two calls.
            // Recorded on the create counter instead.
            AppMetrics.SharedStreamCreates.WithLabels("raced_attached").Inc();
            Log.Debug("[SharedStreamManager] Attached to existing entry (race). DavItemId={DavItemId}", davItemId);
            return handle;
        }

        // No entry covers this position. Previously this returned null and the caller built a
        // private BufferedSegmentStream — which is why playback, always GB downstream of the
        // header-anchored entry, never shared anything. Now a second region gets its own entry,
        // bounded by MaxEntriesPerFile.
        //
        // The guard this replaces was written to avoid a wasted fetch pipeline on the click-to-play
        // path, but the fallback it protected is not cheap: this branch is only reached for
        // open-ended requests (NzbFileStream skips the shared path entirely when a Range end is
        // set), so the alternative is a full private BufferedSegmentStream with its own multi-
        // hundred-MB prefetch window. An entry costs that same inner stream plus one ring buffer,
        // and unlike the private stream it can be shared. Creating one is not the more expensive
        // option; it was only cheaper to skip when the entry was immediately torn down again.
        if (CountEntries(davItemId) >= MaxEntriesPerFile)
        {
            AppMetrics.SharedStreamCreates.WithLabels("at_entry_cap").Inc();
            Log.Debug("[SharedStreamManager] File already has {Cap} entries; caller will use a private stream. DavItemId={DavItemId}, Position={Position}",
                MaxEntriesPerFile, davItemId, startPosition);
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

            // Publish, re-checking the cap under the lock. The factory runs OUTSIDE the lock (it
            // starts a real fetch pipeline and would block every attach for this file), so a
            // concurrent create for the same file can have filled the last slot meanwhile. That
            // window is the same one the old TryAdd race covered, and it is handled the same way:
            // drop ours and take theirs if it fits.
            var published = false;
            var list = s_entries.GetOrAdd(davItemId, _ => new List<SharedStreamEntry>());
            lock (list)
            {
                if (list.Count < MaxEntriesPerFile)
                {
                    list.Add(entry);
                    published = true;
                }
            }

            if (!published)
            {
                // Don't call Dispose — that evicts through the manager, and this entry was never
                // registered. DisposeWithoutEvict releases the slot without touching the list.
                entry.DisposeWithoutEvict();
                foreach (var winner in Snapshot(davItemId))
                {
                    var winnerHandle = winner.TryAttachReader(startPosition);
                    if (winnerHandle == null) continue;
                    AppMetrics.SharedStreamCreates.WithLabels("lost_race_attached").Inc();
                    return winnerHandle;
                }
                AppMetrics.SharedStreamCreates.WithLabels("lost_race_unattachable").Inc();
                return null;
            }

            // Register the first reader's position for backpressure tracking
            var handleId = entry.RegisterReader(startPosition);
            entry.StartPump();
            RefreshEntryGauge();
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
    /// Remove one entry from the manager. Called by SharedStreamEntry on grace period expiry or
    /// failure. Takes the entry itself because a file now holds several.
    /// </summary>
    public static void Evict(Guid davItemId, SharedStreamEntry entry)
    {
        if (!s_entries.TryGetValue(davItemId, out var list)) return;

        bool removed;
        bool emptied;
        lock (list)
        {
            removed = list.Remove(entry);
            emptied = list.Count == 0;
        }

        // Drop the empty list so a file that is no longer streaming leaves nothing behind. Guarded
        // by the same lock a create takes, so this cannot race a concurrent Add into a list that is
        // about to be unpublished.
        if (emptied)
        {
            lock (list)
            {
                if (list.Count == 0) s_entries.TryRemove(new KeyValuePair<Guid, List<SharedStreamEntry>>(davItemId, list));
            }
        }

        if (removed)
        {
            RefreshEntryGauge();
            Log.Debug("[SharedStreamManager] Evicted entry. DavItemId={DavItemId}", davItemId);
        }
    }

    /// <summary>Legacy single-argument overload: evicts every entry for a file.</summary>
    public static void Evict(Guid davItemId)
    {
        foreach (var entry in Snapshot(davItemId)) Evict(davItemId, entry);
    }

    /// <summary>Entries currently registered for one file. Internal so tests can assert on a single
    /// file rather than the global count, which other tests mutate concurrently.</summary>
    internal static int CountEntries(Guid davItemId)
    {
        if (!s_entries.TryGetValue(davItemId, out var list)) return 0;
        lock (list) return list.Count;
    }

    /// <summary>
    /// Get the number of active entries across all files (for diagnostics/debugging).
    /// </summary>
    public static int ActiveEntryCount => s_entries.Values.Sum(CountList);

    private static int CountList(List<SharedStreamEntry> list)
    {
        lock (list) return list.Count;
    }

    private static void RefreshEntryGauge() => AppMetrics.SharedStreamActiveEntries.Set(ActiveEntryCount);

    /// <summary>
    /// Refresh shared-stream gauges. Called periodically by PoolMetricsCollector;
    /// reader counts change on every attach/detach so they are sampled rather than
    /// updated inline at each site.
    /// </summary>
    public static void RefreshGauges()
    {
        var entries = 0;
        var readers = 0;
        foreach (var list in s_entries.Values)
        {
            foreach (var entry in SnapshotList(list))
            {
                entries++;
                readers += entry.ActiveReaders;
            }
        }
        AppMetrics.SharedStreamActiveEntries.Set(entries);
        AppMetrics.SharedStreamActiveReaders.Set(readers);
    }

    private static SharedStreamEntry[] SnapshotList(List<SharedStreamEntry> list)
    {
        lock (list) return list.ToArray();
    }
}
