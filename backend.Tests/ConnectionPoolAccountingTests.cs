using System;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet.Connections;
using Xunit;

namespace NzbWebDAV.Tests;

public class ConnectionPoolAccountingTests
{
    private sealed class FakeConnection : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true; // idempotent, like ThreadSafeNntpClient
    }

    private static ConnectionPool<FakeConnection> CreatePool(
        int maxConnections,
        TimeSpan maxActiveConnectionTime,
        TimeSpan idleTimeout)
    {
        return new ConnectionPool<FakeConnection>(
            maxConnections,
            new ExtendedSemaphoreSlim(maxConnections, maxConnections),
            _ => ValueTask.FromResult(new FakeConnection()),
            poolName: "test-pool-" + Guid.NewGuid().ToString("N"),
            idleTimeout: idleTimeout,
            minWarmConnections: 0,
            maxActiveConnectionTime: maxActiveConnectionTime);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return condition();
    }

    [Fact]
    public async Task DestroyAfterSweeperReclaim_DoesNotDoubleRelease()
    {
        // Regression: the stuck-connection sweeper reclaims a long-held connection (releasing the gate
        // and decrementing _live). When the borrower later disposed its lock with Replace(), Destroy()
        // released a second time — driving _live negative (which inflates AvailableConnections and lets
        // the warm-floor top-up exceed the provider's connection cap) and throwing SemaphoreFullException
        // out of ConnectionLock.Dispose() on the caller's path.
        await using var pool = CreatePool(
            maxConnections: 1,
            maxActiveConnectionTime: TimeSpan.FromMilliseconds(1), // everything is "stuck" immediately
            idleTimeout: TimeSpan.FromMilliseconds(100));          // sweeper runs every 50ms

        using var ctx = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connectionLock = await pool.GetConnectionLockAsync(ctx.Token);

        // Let the sweeper decide this connection is stuck and reclaim it.
        Assert.True(
            await WaitUntil(() => pool.LiveConnections == 0, TimeSpan.FromSeconds(5)),
            "sweeper never reclaimed the stuck connection");

        // The borrower now returns the connection the sweeper already accounted for.
        connectionLock.Replace();
        var ex = Record.Exception(() => connectionLock.Dispose());

        Assert.Null(ex); // pre-fix: SemaphoreFullException
        Assert.Equal(0, pool.LiveConnections); // pre-fix: -1
        Assert.Equal(pool.MaxConnections, pool.AvailableConnections); // pre-fix: 2 for a 1-connection pool
    }

    [Fact]
    public async Task NormalDestroy_ReleasesExactlyOnce()
    {
        // The ordinary replace path must still free its slot, or the pool shrinks by one every time
        // (the v0.7.2 leak).
        await using var pool = CreatePool(
            maxConnections: 1,
            maxActiveConnectionTime: TimeSpan.FromMinutes(30),
            idleTimeout: TimeSpan.FromSeconds(30));

        using var ctx = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 3; i++)
        {
            var connectionLock = await pool.GetConnectionLockAsync(ctx.Token);
            connectionLock.Replace();
            connectionLock.Dispose();
        }

        Assert.Equal(0, pool.LiveConnections);
        Assert.Equal(pool.MaxConnections, pool.AvailableConnections);

        // The slot is genuinely reusable — this would hang/timeout if Destroy had leaked it.
        var final = await pool.GetConnectionLockAsync(ctx.Token);
        Assert.NotNull(final.Connection);
        final.Dispose();
    }
}
