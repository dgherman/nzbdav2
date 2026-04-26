using System.Collections.Concurrent;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Manages a global pool of streaming connections that are shared across all active streams.
/// Instead of each stream having a fixed number of connections, all streams share from a common pool.
/// This means: 1 stream gets all connections, 2 streams get half each, etc.
/// </summary>
public class StreamingConnectionLimiter : IDisposable
{
    private readonly ConfigManager _configManager;
    private SemaphoreSlim _semaphore;
    private int _currentMaxConnections;
    private int _activeStreams;
    private readonly object _lock = new();

    // Static instance for access from non-DI contexts (like BufferedSegmentStream)
    private static StreamingConnectionLimiter? _instance;
    public static StreamingConnectionLimiter? Instance => _instance;

    // Stats for monitoring
    private long _totalAcquires;
    private long _totalReleases;
    private long _totalTimeouts;
    private long _totalForcedReleases;
    private int? _pendingMaxConnections;

    // Track active permits for stuck detection
    private readonly ConcurrentDictionary<string, PermitInfo> _activePermits = new();
    private record PermitInfo(DateTimeOffset AcquiredAt, string? Context, SemaphoreSlim Semaphore);

    // Maximum time a permit can be held before being considered stuck (5 minutes)
    // Reduced from 30 minutes to detect stuck permits faster
    private static readonly TimeSpan MaxPermitHoldTime = TimeSpan.FromMinutes(5);

    // Background sweeper
    private readonly CancellationTokenSource _sweeperCts = new();
    private readonly Task _sweeperTask;

    public StreamingConnectionLimiter(ConfigManager configManager)
    {
        _configManager = configManager;
        _currentMaxConnections = configManager.GetTotalStreamingConnections();
        _semaphore = new SemaphoreSlim(_currentMaxConnections, _currentMaxConnections);
        _instance = this;  // Set static instance
        _sweeperTask = Task.Run(SweeperLoop);  // Start background sweeper
        Log.Information("[StreamingConnectionLimiter] Initialized with {MaxConnections} total streaming connections", _currentMaxConnections);
    }

    /// <summary>
    /// Gets the current number of available permits (connections not in use)
    /// </summary>
    public int AvailableConnections => _semaphore.CurrentCount;

    /// <summary>
    /// Gets the total configured streaming connections
    /// </summary>
    public int TotalConnections => _currentMaxConnections;

    /// <summary>
    /// Gets the number of currently active streams
    /// </summary>
    public int ActiveStreams => _activeStreams;

    /// <summary>
    /// Register a new stream starting. Used for monitoring/stats only.
    /// </summary>
    public void RegisterStream()
    {
        Interlocked.Increment(ref _activeStreams);
        Log.Debug("[StreamingConnectionLimiter] Stream registered. Active streams: {ActiveStreams}, Available: {Available}/{Total}",
            _activeStreams, AvailableConnections, TotalConnections);
    }

    /// <summary>
    /// Unregister a stream that has ended. Used for monitoring/stats only.
    /// </summary>
    public void UnregisterStream()
    {
        Interlocked.Decrement(ref _activeStreams);
        Log.Debug("[StreamingConnectionLimiter] Stream unregistered. Active streams: {ActiveStreams}, Available: {Available}/{Total}",
            _activeStreams, AvailableConnections, TotalConnections);
    }

    /// <summary>
    /// Acquire a streaming connection permit. Blocks until one is available or timeout/cancellation.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for a permit</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="context">Optional context string for debugging stuck permits</param>
    /// <returns>Permit ID if acquired, null if timed out</returns>
    public async Task<string?> AcquireWithTrackingAsync(TimeSpan timeout, CancellationToken ct, string? context = null)
    {
        var lease = await AcquireLeaseAsync(timeout, ct, context).ConfigureAwait(false);
        return lease?.DetachPermitId();
    }

    /// <summary>
    /// Acquire a streaming connection permit as an owned lease. Prefer this API so the
    /// release targets the exact semaphore instance that was acquired, even if config
    /// changes while the permit is held.
    /// </summary>
    public async Task<StreamingConnectionLease?> AcquireLeaseAsync(TimeSpan timeout, CancellationToken ct, string? context = null)
    {
        RefreshConfiguredMax();

        var semaphore = _semaphore;
        var acquired = await semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);
        if (acquired)
        {
            var permitId = Guid.NewGuid().ToString("N");
            _activePermits[permitId] = new PermitInfo(DateTimeOffset.UtcNow, context, semaphore);
            Interlocked.Increment(ref _totalAcquires);
            return new StreamingConnectionLease(this, semaphore, permitId);
        }
        else
        {
            Interlocked.Increment(ref _totalTimeouts);
            Log.Warning("[StreamingConnectionLimiter] Timeout acquiring streaming permit. Active: {Active}, Available: {Available}/{Total}",
                _activeStreams, AvailableConnections, TotalConnections);
            return null;
        }
    }

    /// <summary>
    /// Acquire a streaming connection permit (legacy API without tracking).
    /// </summary>
    /// <param name="timeout">Maximum time to wait for a permit</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if permit was acquired, false if timed out</returns>
    public async Task<bool> AcquireAsync(TimeSpan timeout, CancellationToken ct)
    {
        RefreshConfiguredMax();

        var semaphore = _semaphore;
        var acquired = await semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);
        if (acquired)
        {
            Interlocked.Increment(ref _totalAcquires);
            return true;
        }

        Interlocked.Increment(ref _totalTimeouts);
        Log.Warning("[StreamingConnectionLimiter] Timeout acquiring legacy streaming permit. Active: {Active}, Available: {Available}/{Total}",
            _activeStreams, AvailableConnections, TotalConnections);
        return false;
    }

    /// <summary>
    /// Release a streaming connection permit with tracking.
    /// </summary>
    /// <param name="permitId">The permit ID returned from AcquireWithTrackingAsync</param>
    public void Release(string? permitId)
    {
        if (permitId != null && _activePermits.TryRemove(permitId, out var info))
        {
            ReleaseInternal(info.Semaphore);
            ApplyPendingResizeIfIdle();
            return;
        }
        ReleaseInternal(_semaphore);
        ApplyPendingResizeIfIdle();
    }

    /// <summary>
    /// Release a streaming connection permit (legacy API without tracking).
    /// </summary>
    public void Release()
    {
        // Legacy release - we can't track which permit this is for, so callers
        // should prefer AcquireLeaseAsync. This path intentionally does not add
        // entries to _activePermits, otherwise the sweeper could double-release
        // permits that were already returned through this legacy API.
        ReleaseInternal(_semaphore);
        ApplyPendingResizeIfIdle();
    }

    private void ReleaseInternal(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
            Interlocked.Increment(ref _totalReleases);
        }
        catch (SemaphoreFullException)
        {
            // This shouldn't happen, but log if it does
            Log.Warning("[StreamingConnectionLimiter] Attempted to release more permits than acquired");
        }
    }

    private void ReleaseLease(SemaphoreSlim semaphore, string permitId)
    {
        _activePermits.TryRemove(permitId, out _);
        ReleaseInternal(semaphore);
        ApplyPendingResizeIfIdle();
    }

    private void RefreshConfiguredMax()
    {
        var configuredMax = _configManager.GetTotalStreamingConnections();
        if (configuredMax != _currentMaxConnections)
        {
            ResizeSemaphore(configuredMax);
        }
    }

    /// <summary>
    /// Resize the semaphore when config changes. This is tricky because we can't resize SemaphoreSlim.
    /// We handle this by adjusting the effective limit through tracking.
    /// </summary>
    private void ResizeSemaphore(int newMax)
    {
        lock (_lock)
        {
            if (newMax == _currentMaxConnections) return;

            var oldMax = _currentMaxConnections;
            var available = _semaphore.CurrentCount;
            var inUse = oldMax - available;

            Log.Information("[StreamingConnectionLimiter] Resizing from {Old} to {New} connections. Currently in use: {InUse}",
                oldMax, newMax, inUse);

            if (inUse > 0 || _activePermits.Count > 0)
            {
                _pendingMaxConnections = newMax;
                Log.Information("[StreamingConnectionLimiter] Deferring resize until active permits drain. Pending max: {New}", newMax);
                return;
            }

            ApplyResizeUnderLock(newMax);
        }
    }

    private void ApplyPendingResizeIfIdle()
    {
        lock (_lock)
        {
            if (!_pendingMaxConnections.HasValue) return;
            var inUse = _currentMaxConnections - _semaphore.CurrentCount;
            if (inUse > 0 || _activePermits.Count > 0) return;

            ApplyResizeUnderLock(_pendingMaxConnections.Value);
        }
    }

    private void ApplyResizeUnderLock(int newMax)
    {
        var oldMax = _currentMaxConnections;
        _semaphore = new SemaphoreSlim(newMax, newMax);
        _currentMaxConnections = newMax;
        _pendingMaxConnections = null;
        // Do not dispose the old semaphore here. A concurrent acquire may have
        // captured it immediately before the resize, and active leases release
        // back to the exact semaphore instance they acquired.
        Log.Information("[StreamingConnectionLimiter] Resize applied from {Old} to {New} connections", oldMax, newMax);
    }

    /// <summary>
    /// Get stats for monitoring
    /// </summary>
    public (long Acquires, long Releases, long Timeouts, long ForcedReleases, int Available, int Total, int ActiveStreams, int TrackedPermits) GetStats()
    {
        return (_totalAcquires, _totalReleases, _totalTimeouts, _totalForcedReleases,
                AvailableConnections, TotalConnections, _activeStreams, _activePermits.Count);
    }

    /// <summary>
    /// Background sweeper that detects and releases stuck permits
    /// </summary>
    private async Task SweeperLoop()
    {
        try
        {
            // Check every 30 seconds to detect stuck permits quickly
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(_sweeperCts.Token).ConfigureAwait(false))
            {
                await SweepStuckPermits().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on disposal
        }
    }

    private Task SweepStuckPermits()
    {
        var now = DateTimeOffset.UtcNow;
        var stuckCount = 0;

        foreach (var kvp in _activePermits)
        {
            var permitId = kvp.Key;
            var info = kvp.Value;
            var heldFor = now - info.AcquiredAt;

            if (heldFor > MaxPermitHoldTime)
            {
                Log.Warning(
                    "[StreamingConnectionLimiter] STUCK PERMIT DETECTED: Permit held for {HeldMinutes:F1} minutes. " +
                    "Context: {Context}. Force-releasing to unblock pool.",
                    heldFor.TotalMinutes, info.Context ?? "unknown");

                // Remove from tracking
                if (_activePermits.TryRemove(permitId, out _))
                {
                    // Force-release the semaphore permit
                    try
                    {
                        info.Semaphore.Release();
                        Interlocked.Increment(ref _totalForcedReleases);
                        stuckCount++;
                    }
                    catch (SemaphoreFullException)
                    {
                        Log.Warning("[StreamingConnectionLimiter] Semaphore already full when force-releasing stuck permit");
                    }
                }
            }
        }

        if (stuckCount > 0)
        {
            Log.Warning(
                "[StreamingConnectionLimiter] Force-released {Count} stuck permits. " +
                "Available now: {Available}/{Total}",
                stuckCount, AvailableConnections, TotalConnections);
        }

        return Task.CompletedTask;
    }

    public sealed class StreamingConnectionLease : IDisposable
    {
        private readonly StreamingConnectionLimiter _owner;
        private readonly SemaphoreSlim _semaphore;
        private string? _permitId;

        internal StreamingConnectionLease(StreamingConnectionLimiter owner, SemaphoreSlim semaphore, string permitId)
        {
            _owner = owner;
            _semaphore = semaphore;
            _permitId = permitId;
        }

        internal string DetachPermitId()
        {
            var permitId = _permitId ?? throw new ObjectDisposedException(nameof(StreamingConnectionLease));
            _permitId = null;
            return permitId;
        }

        public void Dispose()
        {
            var permitId = Interlocked.Exchange(ref _permitId, null);
            if (permitId != null)
            {
                _owner.ReleaseLease(_semaphore, permitId);
            }
        }
    }

    public void Dispose()
    {
        _sweeperCts.Cancel();
        try { _sweeperTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _sweeperCts.Dispose();
        _semaphore.Dispose();
    }
}
