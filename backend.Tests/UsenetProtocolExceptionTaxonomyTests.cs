using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using Usenet.Nzb;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// UsenetProtocolException was declared as a direct subclass of Exception — a sibling of
/// UsenetException, not a subtype — so it slipped past every catch filter that decides whether
/// a connection is unhealthy (`is UsenetException`). A protocol-desync error (malformed/empty
/// NNTP response line) on a live connection would fall through those filters uncaught by the
/// connection-health logic: the circuit breaker never saw the failure, and the connection was
/// handed back to the idle pool as if nothing happened, instead of being replaced. See the
/// taxonomy fix in UsenetProtocolException.cs (now : UsenetException).
/// </summary>
public class UsenetProtocolExceptionTaxonomyTests
{
    private sealed class ThrowingNntpClient : INntpClient
    {
        public Exception ExceptionToThrow { get; set; } = new UsenetProtocolException("Invalid NNTP Response: ");

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => Task.FromResult(true);
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct) => throw ExceptionToThrow;
        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct) => throw NotSupportedExceptionOrThrow();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<NzbWebDAV.Clients.Usenet.Models.UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }

        private Exception NotSupportedExceptionOrThrow() => new NotSupportedException();
    }

    private static (MultiConnectionNntpClient client, ConnectionPool<INntpClient> pool, ThrowingNntpClient stub) BuildProvider()
    {
        var stub = new ThrowingNntpClient();
        var pool = new ConnectionPool<INntpClient>(
            1,
            new ExtendedSemaphoreSlim(1, 1),
            _ => ValueTask.FromResult<INntpClient>(stub),
            poolName: "protocol-exception-test",
            idleTimeout: TimeSpan.FromMinutes(15));
        var client = new MultiConnectionNntpClient(pool, ProviderType.Pooled, providerIndex: 0, host: "test-provider");
        return (client, pool, stub);
    }

    /// <summary>
    /// Drives a single attempt through the exact production connection-usage path
    /// (MultiConnectionNntpClient.RunStreamWithConnection, the method GetSegmentStreamAsync calls)
    /// with retries suppressed so the test observes one failure deterministically instead of
    /// racing the production 500ms retry backoff.
    /// </summary>
    private static async Task<Exception> RunOneStreamAttempt(MultiConnectionNntpClient client, CancellationToken ct)
    {
        var method = typeof(MultiConnectionNntpClient).GetMethod("RunStreamWithConnection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Func<INntpClient, CancellationToken, Task<YencHeaderStream>> task =
            (connection, token) => connection.GetSegmentStreamAsync("segment-id", true, token);

        var resultTask = (Task<YencHeaderStream>)method.Invoke(client, new object[] { task, ct, 0, false })!;
        try
        {
            await resultTask;
            throw new InvalidOperationException("Expected the stub connection to throw.");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void UsenetProtocolException_IsAUsenetException()
    {
        // The whole fix is this widening: every catch filter in the codebase that treats a
        // connection as unhealthy checks `is UsenetException`, so UsenetProtocolException must
        // be one to participate.
        Assert.IsAssignableFrom<UsenetException>(new UsenetProtocolException("Invalid NNTP Response: "));
    }

    [Fact]
    public async Task ProtocolException_RecordsCircuitBreakerFailure_AndDoesNotReturnConnectionToIdlePoolAsHealthy()
    {
        var (client, pool, _) = BuildProvider();
        using var cts = new CancellationTokenSource();
        using var scope = cts.Token.SetScopedContext(
            new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, new ConnectionUsageDetails()));

        var ex = await RunOneStreamAttempt(client, cts.Token);

        // Before the fix, UsenetProtocolException fell through the `is UsenetException` catch
        // filter entirely: the circuit breaker never recorded the failure, and the finally block's
        // fallback cleanup (a WaitForReady that only replaces the connection if it faults) pushed
        // the still-desynced connection straight back onto the idle stack as healthy.
        Assert.IsType<UsenetProtocolException>(ex);
        Assert.Equal(0, pool.IdleConnections);

        // Three consecutive failures trip the circuit breaker (ProviderCircuitBreaker.FailureThreshold).
        await RunOneStreamAttempt(client, cts.Token);
        await RunOneStreamAttempt(client, cts.Token);

        Assert.True(client.IsTripped);
    }
}
