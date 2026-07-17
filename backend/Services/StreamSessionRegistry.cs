using System.Collections.Concurrent;
using System.Diagnostics;
using NzbWebDAV.Database.Models;

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

    /// <summary>
    /// Enriches raw sessions with cumulative per-provider byte tallies (from affinity stats,
    /// which are per-title and persisted — NOT per-session) and maps provider indices to the
    /// current config's hosts, dropping stale indices from prior configs.
    /// </summary>
    public static IReadOnlyList<ActiveStreamDto> BuildDtos(
        IReadOnlyList<ActiveStreamSnapshot> sessions,
        Func<string, Dictionary<int, NzbProviderStats>> jobStatsLookup,
        IReadOnlyList<string> providerHosts)
    {
        var dtos = new List<ActiveStreamDto>(sessions.Count);
        foreach (var s in sessions)
        {
            var jobStats = jobStatsLookup(s.AffinityKey);
            var tallies = jobStats
                .Where(kv => kv.Key >= 0 && kv.Key < providerHosts.Count)
                .Where(kv => kv.Value.TotalBytes > 0)
                .Select(kv => new StreamProviderTally(kv.Key, providerHosts[kv.Key], kv.Value.TotalBytes))
                .OrderByDescending(t => t.TotalBytes)
                .ToList();

            var progress = s.FileSize > 0
                ? (int)Math.Clamp(s.CurrentBytePosition * 100 / s.FileSize, 0, 100)
                : 0;

            dtos.Add(new ActiveStreamDto(
                s.DavItemId, s.FileName, s.CurrentBytePosition, s.FileSize, progress, tallies));
        }
        return dtos;
    }
}

public sealed record StreamProviderTally(int ProviderIndex, string Host, long TotalBytes);

public sealed record ActiveStreamDto(
    Guid DavItemId,
    string FileName,
    long CurrentBytePosition,
    long FileSize,
    int ProgressPercent,
    IReadOnlyList<StreamProviderTally> Providers);

public sealed record ActiveStreamSnapshot(
    Guid DavItemId,
    string FileName,
    string AffinityKey,
    long CurrentBytePosition,
    long FileSize);
