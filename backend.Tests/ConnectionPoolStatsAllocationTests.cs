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
    /// Fires the pool-changed handler the way a real stream does — with a pool full of active
    /// connections carrying realistic usage details — and reports bytes allocated per event.
    /// Not an assertion of a magic number; it prints the cost so a fix can be measured against it.
    /// </summary>
    [Fact]
    public async Task StatsEvent_AllocationPerConnectionEvent()
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

        // Fill the pool with active connections carrying the details a streaming request attaches,
        // so the JSON the handler serializes is the size it would really be mid-playback.
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

        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(connections, 0, connections);

        handler(this, args); // warm up JIT + serializer metadata

        const int iterations = 200;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < iterations; i++) handler(this, args);
        var after = GC.GetTotalAllocatedBytes(precise: true);

        var perEvent = (after - before) / (double)iterations;
        _output.WriteLine($"Allocated per stats event: {perEvent:N0} bytes ({connections} active connections)");
        _output.WriteLine($"Per segment (2 events: borrow+return): {perEvent * 2 / 1024:N1} KiB");
        _output.WriteLine($"Extrapolated over a 5,593-segment file: {perEvent * 2 * 5593 / 1024 / 1024:N0} MiB of garbage");

        foreach (var l in locks) l.Dispose();

        Assert.True(perEvent > 0, "expected the stats event to allocate");
    }
}
