using System;
using System.Threading;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Throttle + single-flight gate for the per-provider latency check.
/// Guarantees at most one latency check runs at a time and that attempts
/// (whether they succeed OR fail) occur no more often than <c>minInterval</c>.
/// Without this, a provider that is unreachable never advances the
/// success-based throttle, so the 10s timer fires an unthrottled ping every
/// tick — the "latency-check storm" that wedged the backend on reboot.
/// Designed for a single timer caller; not a general-purpose primitive.
/// </summary>
public sealed class LatencyCheckGate
{
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private int _inFlight; // 0 = idle, 1 = running

    public LatencyCheckGate(TimeSpan minInterval) => _minInterval = minInterval;

    /// <summary>
    /// Attempts to begin a latency check. On <c>true</c>, records <paramref name="now"/>
    /// as the attempt time and marks in-flight; the caller MUST call <see cref="End"/>
    /// when the check completes (in a finally). Returns <c>false</c> when throttled or
    /// when a check is already in flight.
    /// </summary>
    public bool TryBegin(DateTimeOffset now)
    {
        if (now - _lastAttempt <= _minInterval) return false;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return false;
        _lastAttempt = now;
        return true;
    }

    /// <summary>Releases the single-flight flag. Call once per successful <see cref="TryBegin"/>.</summary>
    public void End() => Interlocked.Exchange(ref _inFlight, 0);
}
