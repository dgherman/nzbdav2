using System.Collections.Concurrent;
using System.Diagnostics;

namespace NzbWebDAV.Services;

/// <summary>
/// Heartbeat registry of in-progress stream sessions, keyed by DavItem so that seeks and
/// multipart parts of the same file collapse to a single session row. Entries are upserted from
/// the streaming path's existing UpdateUsageContext and expire by TTL, so correctness never
/// depends on catching a stream's dispose path.
/// </summary>
public sealed class StreamSessionRegistry
{
    private sealed class Entry
    {
        public string FileName = "";
        public string AffinityKey = "";
        public long CurrentBytePosition;
        public long FileSize;
        public long LastTouchedTimestamp;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Most recently constructed instance, reached from the manually-constructed
    /// BufferedSegmentStream which is not DI-managed. Mirrors BufferedSegmentStream's static config.</summary>
    public static StreamSessionRegistry? Current { get; private set; }

    public StreamSessionRegistry()
    {
        Current = this;
    }

    public void Touch(Guid davItemId, string fileName, string affinityKey, long currentBytePosition, long fileSize)
    {
        var entry = _sessions.GetOrAdd(davItemId, _ => new Entry());
        entry.FileName = fileName;
        entry.AffinityKey = affinityKey;
        entry.CurrentBytePosition = currentBytePosition;
        entry.FileSize = fileSize;
        entry.LastTouchedTimestamp = Stopwatch.GetTimestamp();
    }

    public IReadOnlyList<ActiveStreamSnapshot> GetActiveSessions()
    {
        var result = new List<ActiveStreamSnapshot>();
        foreach (var (davItemId, entry) in _sessions)
        {
            if (Stopwatch.GetElapsedTime(entry.LastTouchedTimestamp) > Ttl)
            {
                _sessions.TryRemove(davItemId, out _); // opportunistic sweep bounds the dict
                continue;
            }
            result.Add(new ActiveStreamSnapshot(
                davItemId, entry.FileName, entry.AffinityKey, entry.CurrentBytePosition, entry.FileSize));
        }
        return result;
    }
}

public sealed record ActiveStreamSnapshot(
    Guid DavItemId,
    string FileName,
    string AffinityKey,
    long CurrentBytePosition,
    long FileSize);
