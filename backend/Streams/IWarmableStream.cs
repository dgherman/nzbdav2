namespace NzbWebDAV.Streams;

/// <summary>
/// A stream that can begin its background prefetch before anyone reads from it.
/// <para>
/// CombinedStream uses this to warm the next RAR volume's connections and read-ahead buffer while
/// the current volume is still being consumed, so the swap at the volume boundary finds a stream
/// that is already fetching instead of starting cold. Constructing a part's stream object (via its
/// factory) is cheap and does no I/O — for NzbFileStream specifically, the network fetch only starts
/// once something reads from it. WarmupAsync is that "start fetching" trigger without discarding any
/// bytes, so the pre-warmed stream is still positioned at its start when the real read arrives.
/// </para>
/// </summary>
public interface IWarmableStream
{
    /// <summary>Begins background prefetching, if not already started, without consuming any bytes.</summary>
    Task WarmupAsync(CancellationToken ct);
}
