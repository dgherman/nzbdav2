namespace NzbWebDAV.Streams;

/// <summary>
/// A stream that can be marked alive without being read from.
/// <para>
/// BufferedSegmentStream self-cancels its workers when no read arrives for a while, which is how an
/// orphaned stream (client gone, but the HTTP write not yet failed) releases its permits. A pump that
/// is deliberately paused on ring-buffer backpressure looks identical from the outside, so it needs a
/// way to say "still wanted, just not reading right now".
/// </para>
/// </summary>
public interface ITouchableStream
{
    /// <summary>Resets the idle timer without consuming data.</summary>
    void Touch();
}
