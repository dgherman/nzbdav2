using System.Runtime.CompilerServices;
using System.Text;

namespace NzbWebDAV.Streams;

/// <summary>
/// Counters for the segment-buffer rent/return path, used to measure whether
/// ArrayPool is actually reusing buffers (issue #19). Distinct identities is the
/// number that matters: if it tracks the rent count, every rent allocated a fresh
/// array and the pool is providing no reuse.
///
/// Off unless NZBDAV_POOL_DIAG=1, since it takes a lock on every rent.
/// </summary>
internal static class SegmentBufferPoolDiagnostics
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("NZBDAV_POOL_DIAG") == "1";

    private static long _rents;
    private static long _returns;
    private static long _resizeRents;
    private static long _outstanding;
    private static long _peakOutstanding;
    private static readonly HashSet<int> DistinctArrays = new();
    private static readonly Dictionary<int, long> RentedLengths = new();
    private static readonly HashSet<int> RentThreads = new();
    private static readonly HashSet<int> ReturnThreads = new();
    private static readonly object Sync = new();

    public static void RecordRent(byte[] buffer)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _rents);

        // High-water mark of simultaneously checked-out buffers: this is the working set the
        // pool has to satisfy, and what a dedicated pool would need to be sized for.
        var outstanding = Interlocked.Increment(ref _outstanding);
        long peak;
        while (outstanding > (peak = Interlocked.Read(ref _peakOutstanding)))
        {
            if (Interlocked.CompareExchange(ref _peakOutstanding, outstanding, peak) == peak) break;
        }

        lock (Sync)
        {
            DistinctArrays.Add(RuntimeHelpers.GetHashCode(buffer));
            RentedLengths.TryGetValue(buffer.Length, out var count);
            RentedLengths[buffer.Length] = count + 1;
            RentThreads.Add(Environment.CurrentManagedThreadId);
        }
    }

    /// <summary>
    /// A resize is a rent like any other and must be accounted as one. Recording only the resize
    /// counter (as this did) left the outstanding tally one short for every segment that grew, so
    /// "peak checked out" — the number the pool is sized from — read low.
    /// </summary>
    public static void RecordResizeRent(byte[] buffer)
    {
        if (!Enabled) return;
        RecordRent(buffer);
        Interlocked.Increment(ref _resizeRents);
    }

    public static void RecordReturn()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _returns);
        Interlocked.Decrement(ref _outstanding);
        lock (Sync)
        {
            ReturnThreads.Add(Environment.CurrentManagedThreadId);
        }
    }

    public static string Report()
    {
        var report = new StringBuilder();
        lock (Sync)
        {
            var rents = Interlocked.Read(ref _rents);
            var distinct = DistinctArrays.Count;
            var reusePercent = rents > 0 ? 100.0 * (rents - distinct) / rents : 0;
            report.AppendLine("  SEGMENT BUFFER POOL:");
            report.AppendLine($"    Rents:              {rents,8}");
            report.AppendLine($"    Returns:            {Interlocked.Read(ref _returns),8}");
            report.AppendLine($"    Resize re-rents:    {Interlocked.Read(ref _resizeRents),8}");
            report.AppendLine($"    Peak checked out:   {Interlocked.Read(ref _peakOutstanding),8}   <- working set the pool must satisfy");
            report.AppendLine($"    Still checked out:  {Interlocked.Read(ref _outstanding),8}");
            report.AppendLine($"    Distinct arrays:    {distinct,8}   <- fresh allocations");
            report.AppendLine($"    Pool reuse rate:    {reusePercent,7:F1}%");
            report.AppendLine($"    Rent/return threads:{RentThreads.Count,4} /{ReturnThreads.Count,3}");
            foreach (var (length, count) in RentedLengths.OrderByDescending(x => x.Value))
            {
                report.AppendLine($"      rented {length,9} bytes x {count}");
            }
        }

        return report.ToString().TrimEnd('\n', '\r');
    }
}
