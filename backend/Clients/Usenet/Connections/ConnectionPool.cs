using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NzbWebDAV.Extensions;
using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Thread-safe, lazy connection pool.
/// <para>
/// *  Connections are created through a user-supplied factory (sync or async).<br/>
/// *  At most <c>maxConnections</c> live instances exist at any time.<br/>
/// *  Idle connections older than <see cref="IdleTimeout"/> are disposed
///    automatically by a background sweeper.<br/>
/// *  <see cref="Dispose"/> / <see cref="DisposeAsync"/> stop the sweeper and
///    dispose all cached connections.  Borrowed handles returned afterwards are
///    destroyed immediately.
/// *  Note: This class was authored by ChatGPT 3o
/// </para>
/// </summary>
public sealed class ConnectionPool<T> : IDisposable, IAsyncDisposable
{
    /* -------------------------------- configuration -------------------------------- */

    public TimeSpan IdleTimeout { get; }
    public string PoolName { get; }
    public int LiveConnections => _live;
    public int IdleConnections => _idleConnections.Count;
    public int ActiveConnections => _live - _idleConnections.Count;
    public int AvailableConnections => _maxConnections - ActiveConnections;
    public int MaxConnections => _maxConnections;
    public int RemainingSemaphoreSlots => _gate.RemainingSemaphoreSlots;

    public event EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs>? OnConnectionPoolChanged;

    private readonly Func<CancellationToken, ValueTask<T>> _factory;
    private readonly int _maxConnections;

    /* --------------------------------- state --------------------------------------- */

    private readonly ConcurrentStack<Pooled> _idleConnections = new();
    private readonly CombinedSemaphoreSlim _gate;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweeperTask; // keeps timer alive

    private int _live; // number of connections currently alive
    private int _disposed; // 0 == false, 1 == true

    // Track active connections by usage type
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActiveConnectionInfo> _activeConnections = new();
    private record ActiveConnectionInfo(T Connection, ConnectionUsageContext Context, DateTimeOffset BorrowedAt);

    // Maximum time a connection can be held before being considered stuck (30 minutes)
    private static readonly TimeSpan MaxActiveConnectionTime = TimeSpan.FromMinutes(30);

    // Track doomed connections (marked for release)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<T, bool> _doomedConnections = new();

    // Circuit breaker state
    private int _consecutiveConnectionFailures;
    private DateTimeOffset _lastConnectionFailure = DateTimeOffset.MinValue;

    /* ------------------------------------------------------------------------------ */

    public ConnectionPool(
        int maxConnections,
        ExtendedSemaphoreSlim pooledSemaphore,
        Func<CancellationToken, ValueTask<T>> connectionFactory,
        string poolName = "Unknown",
        TimeSpan? idleTimeout = null)
    {
        if (maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));

        _factory = connectionFactory
                   ?? throw new ArgumentNullException(nameof(connectionFactory));
        IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        PoolName = poolName;

        _maxConnections = maxConnections;
        _gate = new CombinedSemaphoreSlim(maxConnections, pooledSemaphore);
        _sweeperTask = Task.Run(SweepLoop); // background idle-reaper
    }

    /* ============================== public API ==================================== */

    /// <summary>
    /// Borrow a connection while reserving capacity for higher-priority callers.
    /// Waits until at least (`reservedCount` + 1) slots are free before acquiring one,
    /// ensuring that after acquisition at least `reservedCount` remain available.
    /// </summary>
    public async Task<ConnectionLock<T>> GetConnectionLockAsync(
        CancellationToken cancellationToken = default)
    {
        var usageContext = cancellationToken.GetContext<ConnectionUsageContext>();


        // Determine if we need to reserve slots for higher-priority callers.
        // Background tasks (HealthCheck, Repair) must leave some capacity available.
        // We reserve 25% of the pool for Streaming and Queue.
        var reservedSlots = 0;
        if (usageContext.UsageType == ConnectionUsageType.HealthCheck ||
            usageContext.UsageType == ConnectionUsageType.Repair)
        {
            // Reserve ~16% of slots for high-priority traffic.
            // This allows background tasks to balloon up to ~84% utilization
            // while keeping a buffer for streaming/queue starts.
            reservedSlots = Math.Max(1, _maxConnections / 6);
        }

        // Make caller cancellation also cancel the wait on the gate.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sweepCts.Token);

        await _gate.WaitAsync(reservedSlots, linked.Token).ConfigureAwait(false);

        // Pool might have been disposed after wait returned:
        if (Volatile.Read(ref _disposed) == 1)
        {
            _gate.Release();
            ThrowDisposed();
        }

        // Generate connection ID for tracking
        var connectionId = Guid.NewGuid().ToString();

        // Try to reuse an existing idle connection.
        while (_idleConnections.TryPop(out var item))
        {
            if (item.IsExpired(IdleTimeout))
            {
                // Stale – destroy and continue looking.
                await DisposeConnectionAsync(item.Connection).ConfigureAwait(false);
                Interlocked.Decrement(ref _live);
                TriggerConnectionPoolChangedEvent();
                continue;
            }

            // Health check for long-idle connections (>60s) before reuse
            // Increased from 30s to 60s to reduce unnecessary health checks
            // This still catches stale connections while reducing overhead
            if (unchecked(Environment.TickCount64 - item.LastTouchedMillis) > 60000 && item.Connection is INntpClient client)
            {
                try
                {
                    // Quick liveness check with 3s timeout (reduced from 5s for faster failure detection)
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await client.DateAsync(cts.Token).ConfigureAwait(false);
                    Serilog.Log.Debug("[ConnectionPool][{PoolName}] Health check passed for idle connection (idle: {IdleTime:F0}s)", PoolName, (Environment.TickCount64 - item.LastTouchedMillis) / 1000.0);
                }
                catch (Exception ex)
                {
                    // Failed check - discard
                    Serilog.Log.Warning("[ConnectionPool][{PoolName}] Health check failed for idle connection (idle: {IdleTime:F0}s), discarding: {Error}", PoolName, (Environment.TickCount64 - item.LastTouchedMillis) / 1000.0, ex.Message);
                    await DisposeConnectionAsync(item.Connection).ConfigureAwait(false);
                    Interlocked.Decrement(ref _live);
                    TriggerConnectionPoolChangedEvent();
                    continue;
                }
            }

            TriggerConnectionPoolChangedEvent();
            _activeConnections[connectionId] = new ActiveConnectionInfo(item.Connection, usageContext, DateTimeOffset.UtcNow);

            return BuildLock(item.Connection, connectionId);
        }

        // Need a fresh connection.
        T conn = default!;
        var retries = 0;
        const int maxRetries = 5;

        while (true)
        {
            // Circuit breaker check - reduced cooldown from 5s to 2s for faster recovery
            if (_consecutiveConnectionFailures > 5)
            {
                var cooldown = TimeSpan.FromSeconds(2);
                var timeSinceFailure = DateTimeOffset.UtcNow - _lastConnectionFailure;
                if (timeSinceFailure < cooldown)
                {
                    var wait = cooldown - timeSinceFailure;
                    Serilog.Log.Warning("[ConnectionPool][{PoolName}] Circuit breaker active ({Failures} consecutive failures). Pausing for {Wait:F1}s.", PoolName, _consecutiveConnectionFailures, wait.TotalSeconds);
                    await Task.Delay(wait, linked.Token).ConfigureAwait(false);
                }
            }

            try
            {
                Serilog.Log.Debug("[ConnectionPool][{PoolName}] Creating new connection (attempt {Retries}/{MaxRetries})...", PoolName, retries + 1, maxRetries);
                conn = await _factory(linked.Token).ConfigureAwait(false);

                // Reset circuit breaker on success
                var previousFailures = Interlocked.Exchange(ref _consecutiveConnectionFailures, 0);
                if (previousFailures > 0)
                {
                    Serilog.Log.Information("[ConnectionPool][{PoolName}] Connection created successfully after {Failures} previous failures. Circuit breaker reset.", PoolName, previousFailures);
                }
                break;
            }
            catch (Exception ex)
            {
                // Update circuit breaker stats
                var currentFailures = Interlocked.Increment(ref _consecutiveConnectionFailures);
                _lastConnectionFailure = DateTimeOffset.UtcNow;
                // Check for socket exhaustion errors (AddressInUse, TryAgain/EAGAIN)
                // We check string message too because sometimes it's wrapped or platform specific
                var isSocketExhaustion = ex.ToString().Contains("Address already in use") || 
                                         ex.ToString().Contains("Resource temporarily unavailable") ||
                                         (ex is System.Net.Sockets.SocketException sockEx && 
                                            (sockEx.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse || 
                                             sockEx.SocketErrorCode == System.Net.Sockets.SocketError.TryAgain));

                if (isSocketExhaustion && retries < maxRetries)
                {
                    retries++;
                    var delay = 100 * (1 << (retries - 1)); // Exponential backoff: 100, 200, 400, 800, 1600 ms
                    Serilog.Log.Warning("[ConnectionPool][{PoolName}] Socket exhaustion detected (EAGAIN/AddressInUse). Retrying in {Delay}ms (Attempt {Retry}/{Max}, Failures: {Failures})...", PoolName, delay, retries, maxRetries, currentFailures);

                    try
                    {
                        await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _gate.Release();
                        throw;
                    }
                    continue;
                }

                // Log the error with circuit breaker context
                if (currentFailures > 3)
                {
                    Serilog.Log.Error("[ConnectionPool][{PoolName}] Failed to create connection for {UsageType} (Failure #{Failures}). Circuit breaker will activate after 5 failures. Error: {Error}", PoolName, usageContext.UsageType, currentFailures, ex.Message);
                }
                else
                {
                    Serilog.Log.Warning("[ConnectionPool][{PoolName}] Failed to create connection for {UsageType}: {Error}", PoolName, usageContext.UsageType, ex.Message);
                }

                _gate.Release(); // free the permit on failure
                throw;
            }
        }

        Interlocked.Increment(ref _live);
        TriggerConnectionPoolChangedEvent();

        _activeConnections[connectionId] = new ActiveConnectionInfo(conn, usageContext, DateTimeOffset.UtcNow);
        return BuildLock(conn, connectionId);

        ConnectionLock<T> BuildLock(T c, string connId)
            => new(c, conn => Return(conn, connId), conn => Destroy(conn, connId));

        static void ThrowDisposed()
            => throw new ObjectDisposedException(nameof(ConnectionPool<T>));
    }

    /// <summary>
    /// Gracefully marks active connections matching the given type (or all if null) for release.
    /// They will be disposed when returned to the pool.
    /// </summary>
    public Task ForceReleaseConnections(ConnectionUsageType? type = null)
    {
        var targets = _activeConnections
            .Where(x => type == null || x.Value.Context.UsageType == type.Value)
            .ToList();

        foreach (var target in targets)
        {
            // Mark as doomed. Return() will check this list and dispose instead of pooling.
            _doomedConnections.TryAdd(target.Value.Connection, true);
        }
        
        return Task.CompletedTask;
    }

    /* ========================== core helpers ====================================== */

    private readonly record struct Pooled(T Connection, long LastTouchedMillis)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExpired(TimeSpan idle, long nowMillis = 0)
        {
            if (nowMillis == 0) nowMillis = Environment.TickCount64;
            return unchecked(nowMillis - LastTouchedMillis) >= idle.TotalMilliseconds;
        }
    }

    private void Return(T connection, string connectionId)
    {
        _activeConnections.TryRemove(connectionId, out var info);
        var usageType = info?.Context.UsageType ?? ConnectionUsageType.Unknown;

        if (Volatile.Read(ref _disposed) == 1 || _doomedConnections.TryRemove(connection, out _))
        {
            _ = DisposeConnectionAsync(connection); // fire & forget
            Interlocked.Decrement(ref _live);
            TriggerConnectionPoolChangedEvent();
            return;
        }

        _idleConnections.Push(new Pooled(connection, Environment.TickCount64));
        _gate.Release();
        TriggerConnectionPoolChangedEvent();
    }

    private void Destroy(T connection, string connectionId)
    {
        _activeConnections.TryRemove(connectionId, out var info);
        var usageType = info?.Context.UsageType ?? ConnectionUsageType.Unknown;

        // When a lock requests replacement, we dispose the connection instead of reusing.
        _ = DisposeConnectionAsync(connection); // fire & forget
        Interlocked.Decrement(ref _live);
        if (Volatile.Read(ref _disposed) == 0)
        {
            _gate.Release();
        }

        TriggerConnectionPoolChangedEvent();
    }

    public Dictionary<ConnectionUsageType, int> GetUsageBreakdown()
    {
        return _activeConnections.Values
            .GroupBy(x => x.Context.UsageType)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public List<ConnectionUsageContext> GetActiveConnections()
    {
        return _activeConnections.Values.Select(x => x.Context).ToList();
    }

    private string GetUsageBreakdownString()
    {
        var breakdown = GetUsageBreakdown();
        var parts = breakdown
            .OrderBy(x => x.Key)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToArray();
        return parts.Length > 0 ? string.Join(",", parts) : "none";
    }

    public void TriggerStatsUpdate()
    {
        TriggerConnectionPoolChangedEvent();
    }

    private void TriggerConnectionPoolChangedEvent()
    {
        OnConnectionPoolChanged?.Invoke(this, new ConnectionPoolStats.ConnectionPoolChangedEventArgs(
            _live,
            _idleConnections.Count,
            _maxConnections
        ));
    }

    /* =================== idle sweeper (background) ================================= */

    private async Task SweepLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(IdleTimeout / 2);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
                await SweepOnce().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* normal on disposal */
        }
    }

    private async Task SweepOnce()
    {
        var now = Environment.TickCount64;
        var survivors = new List<Pooled>();
        var isAnyConnectionFreed = false;

        // Sweep idle connections
        while (_idleConnections.TryPop(out var item))
        {
            if (item.IsExpired(IdleTimeout, now))
            {
                await DisposeConnectionAsync(item.Connection).ConfigureAwait(false);
                Interlocked.Decrement(ref _live);
                isAnyConnectionFreed = true;
            }
            else
            {
                survivors.Add(item);
            }
        }

        // Preserve original LIFO order.
        for (int i = survivors.Count - 1; i >= 0; i--)
            _idleConnections.Push(survivors[i]);

        // Detect and handle stuck active connections
        var utcNow = DateTimeOffset.UtcNow;
        foreach (var kvp in _activeConnections)
        {
            var connectionId = kvp.Key;
            var info = kvp.Value;
            var heldFor = utcNow - info.BorrowedAt;

            if (heldFor > MaxActiveConnectionTime)
            {
                Serilog.Log.Warning(
                    "[ConnectionPool][{PoolName}] STUCK CONNECTION DETECTED: Connection held for {HeldMinutes:F1} minutes. " +
                    "Usage: {UsageType}, Details: {Details}. Marking for forced release.",
                    PoolName, heldFor.TotalMinutes, info.Context.UsageType, info.Context.Details);

                // Mark the connection as doomed - it will be disposed when/if it's ever returned
                _doomedConnections.TryAdd(info.Connection, true);

                // Remove from active tracking (the connection is leaked, but we free the tracking slot)
                if (_activeConnections.TryRemove(connectionId, out _))
                {
                    // Release the semaphore permit to allow new connections
                    // The actual connection is leaked but at least we unblock the pool
                    _gate.Release();
                    Interlocked.Decrement(ref _live);
                    isAnyConnectionFreed = true;

                    Serilog.Log.Warning(
                        "[ConnectionPool][{PoolName}] Forced release of stuck connection. Pool permit released. " +
                        "Connection may be leaked but pool is unblocked.",
                        PoolName);
                }
            }
        }

        if (isAnyConnectionFreed)
            TriggerConnectionPoolChangedEvent();
    }

    /* ------------------------- dispose helpers ------------------------------------ */

    private static async ValueTask DisposeConnectionAsync(T conn)
    {
        switch (conn)
        {
            case IAsyncDisposable ad:
                await ad.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable d:
                d.Dispose();
                break;
        }
    }

    /* -------------------------- IAsyncDisposable ---------------------------------- */

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _sweepCts.Cancel();

        try
        {
            await _sweeperTask.ConfigureAwait(false); // await clean sweep exit
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }

        // Drain and dispose cached items.
        while (_idleConnections.TryPop(out var item))
            await DisposeConnectionAsync(item.Connection).ConfigureAwait(false);

        _sweepCts.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /* ----------------------------- IDisposable ------------------------------------ */

    public void Dispose()
    {
        _ = DisposeAsync().AsTask(); // fire-and-forget synchronous path
    }
}