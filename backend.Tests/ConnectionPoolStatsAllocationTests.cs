using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;
using Usenet.Nzb;
using UsenetSharp.Models;
using Xunit;
using Xunit.Abstractions;

namespace NzbWebDAV.Tests;

/// <summary>
/// Quantifies the garbage produced by the connection-pool stats event, which fires on every
/// connection borrow and return. See issue #14: streaming is churn-bound, not leak-bound, so what
/// kills a long stream is allocation per connection event multiplied by segments fetched.
/// </summary>
public class ConnectionPoolStatsAllocationTests
{
    private readonly ITestOutputHelper _output;
    public ConnectionPoolStatsAllocationTests(ITestOutputHelper output) => _output = output;

    /// <summary>An open socket that swallows sends — stands in for a browser with the UI open.</summary>
    private sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
    {
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
            => new TaskCompletionSource<System.Net.WebSockets.WebSocketReceiveResult>().Task;
        public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType t, bool eom, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StubNntpClient : INntpClient
    {
        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => Task.FromResult(true);
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<NzbWebDAV.Streams.YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<NzbWebDAV.Clients.Usenet.Models.UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static UsenetProviderConfig BuildConfig(int maxConnections) => new()
    {
        Providers = new List<UsenetProviderConfig.ConnectionDetails>
        {
            new()
            {
                Host = "news.example.com",
                Port = 563,
                UseSsl = true,
                User = "u",
                Pass = "p",
                MaxConnections = maxConnections,
                Type = ProviderType.Pooled,
            }
        }
    };

    /// <summary>
    /// Builds a pool full of active connections carrying the usage details a streaming request
    /// attaches, so the JSON the handler serializes is the size it would really be mid-playback.
    /// </summary>
    private static async Task<List<ConnectionLock<INntpClient>>> FillPoolAsync(
        ConnectionPool<INntpClient> pool, int connections)
    {
        var locks = new List<ConnectionLock<INntpClient>>();
        for (var i = 0; i < connections; i++)
        {
            var details = new ConnectionUsageDetails
            {
                Text = "/content/Stremio_TV/House-of-David-S01E08-David-and-Goliath-Part-2-1080p-AMZN-WEB-DL-DD-5-1-H-264-playWEB/House.of.David.S01E08.David.and.Goliath.Part.2.1080p.AMZN.WEB-DL.DD+5.1.H.264-playWEB.mkv",
                JobName = "House.of.David.S01E08.David.and.Goliath.Part.2.1080p.AMZN.WEB-DL.DD+5.1.H.264-playWEB.mkv",
                AffinityKey = "House-of-David-S01E08",
                DavItemId = Guid.NewGuid(),
                FileSize = 4_000_000_000,
                BufferedCount = 42,
                BufferWindowStart = 4100,
                BufferWindowEnd = 4160,
                TotalSegments = 5593,
                CurrentBytePosition = 3_000_000_000,
            };
            using var scope = new CancellationTokenSource().Token.SetScopedContext(
                new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, details));
            locks.Add(await pool.GetConnectionLockAsync(CancellationToken.None));
        }
        return locks;
    }

    /// <summary>
    /// The case that matters for issue #14: a long stream with the UI closed, which is how anyone
    /// watching in Plex actually runs. Nothing can consume the message, so the event must not build
    /// one. Measured at 61,683 bytes per event before the fix — 120.5 KiB per segment, ~658 MiB of
    /// garbage over a 5,593-segment file, all of it discarded unread.
    /// </summary>
    [Fact]
    public async Task StatsEvent_WithNoSubscribers_AllocatesAlmostNothing()
    {
        const int connections = 30; // NAS runs usenet.total-streaming-connections = 30
        var config = BuildConfig(connections);
        var websocket = new WebsocketManager();
        var stats = new ConnectionPoolStats(config, websocket);

        await using var pool = new ConnectionPool<INntpClient>(
            connections,
            new ExtendedSemaphoreSlim(connections, connections),
            _ => ValueTask.FromResult<INntpClient>(new StubNntpClient()),
            poolName: "news.example.com",
            idleTimeout: TimeSpan.FromMinutes(15));

        stats.RegisterConnectionPool(0, pool);
        var handler = stats.GetOnConnectionPoolChanged(0);
        var locks = await FillPoolAsync(pool, connections);

        Assert.False(websocket.HasSubscribers);

        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(connections, 0, connections);
        handler(this, args); // warm up JIT + serializer metadata

        // Thread-local, not GC.GetTotalAllocatedBytes: that counts the whole process, and xUnit runs
        // test classes in parallel, so a sibling class's allocations would land in this number. The
        // handler is synchronous, so the measured work stays on this thread.
        const int iterations = 200;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) handler(this, args);
        var after = GC.GetAllocatedBytesForCurrentThread();

        var perEvent = (after - before) / (double)iterations;
        _output.WriteLine($"Allocated per stats event, no subscribers: {perEvent:N0} bytes ({connections} active connections)");
        _output.WriteLine($"Extrapolated over a 5,593-segment file: {perEvent * 2 * 5593 / 1024 / 1024:N1} MiB (was 658 MiB)");

        foreach (var l in locks) l.Dispose();

        // Was 61,683. The remaining cost is the counter update; the 1 KiB ceiling is loose enough
        // not to be brittle and tight enough that rebuilding the message would fail it outright.
        Assert.True(perEvent < 1024,
            $"expected the event to skip building a message nobody can read, but it allocated {perEvent:N0} bytes");
    }

    /// <summary>
    /// With the UI open the message does have to be built, but the event fires thousands of times a
    /// second and no reader can use that. Pushes are coalesced per provider, so a burst costs one
    /// message rather than one per event.
    /// </summary>
    [Fact]
    public async Task StatsEvent_WithSubscriber_CoalescesBurstIntoOnePush()
    {
        const int connections = 30;
        var config = BuildConfig(connections);
        var websocket = new WebsocketManager();
        var stats = new ConnectionPoolStats(config, websocket);

        await using var pool = new ConnectionPool<INntpClient>(
            connections,
            new ExtendedSemaphoreSlim(connections, connections),
            _ => ValueTask.FromResult<INntpClient>(new StubNntpClient()),
            poolName: "news.example.com",
            idleTimeout: TimeSpan.FromMinutes(15));

        stats.RegisterConnectionPool(0, pool);
        var handler = stats.GetOnConnectionPoolChanged(0);
        var locks = await FillPoolAsync(pool, connections);

        websocket.AddSubscriberForTest(new FakeWebSocket());
        Assert.True(websocket.HasSubscribers);

        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(connections, 0, connections);
        handler(this, args); // warm up, and consume the leading-edge push

        // Thread-local: see the sibling test — the process-wide counter picks up parallel classes.
        const int iterations = 200;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) handler(this, args);
        var after = GC.GetAllocatedBytesForCurrentThread();

        var perEvent = (after - before) / (double)iterations;
        _output.WriteLine($"Allocated per stats event, one subscriber: {perEvent:N0} bytes ({connections} active connections)");

        foreach (var l in locks) l.Dispose();

        // A 200-event burst inside one 250ms window collapses to a single trailing push, so the
        // per-event average must stay far below the ~61 KiB an uncoalesced build costs.
        Assert.True(perEvent < 5_000,
            $"expected a burst to coalesce, but it allocated {perEvent:N0} bytes per event");
    }
}
