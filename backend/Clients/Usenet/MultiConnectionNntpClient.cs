using System.IO;
using System.Net.Sockets;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Usenet.Exceptions;
using Usenet.Nntp.Responses;
using Usenet.Nzb;
using Usenet.Yenc;

namespace NzbWebDAV.Clients.Usenet;

public class MultiConnectionNntpClient : INntpClient
{
    public ProviderType ProviderType { get; }
    public int LiveConnections => _connectionPool.LiveConnections;
    public int IdleConnections => _connectionPool.IdleConnections;
    public int ActiveConnections => _connectionPool.ActiveConnections;
    public int AvailableConnections => _connectionPool.AvailableConnections;
    public int RemainingSemaphoreSlots => _connectionPool.RemainingSemaphoreSlots;

    private ConnectionPool<INntpClient> _connectionPool;
    private readonly GlobalOperationLimiter? _globalLimiter;
    private readonly BandwidthService? _bandwidthService;
    private readonly ProviderErrorService? _providerErrorService;
    private readonly int _providerIndex;
    private readonly string _host;
    private readonly int _operationTimeoutSeconds;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastLatencyRecordTime = DateTimeOffset.MinValue;
    private readonly Timer? _latencyMonitorTimer;

    public long AverageLatency => _bandwidthService?.GetAverageLatency(_providerIndex) ?? 0;
    public int ProviderIndex => _providerIndex;
    public string Host => _host;

    public MultiConnectionNntpClient(
        ConnectionPool<INntpClient> connectionPool,
        ProviderType type,
        GlobalOperationLimiter? globalLimiter = null,
        BandwidthService? bandwidthService = null,
        ProviderErrorService? providerErrorService = null,
        int providerIndex = -1,
        string? host = null,
        int operationTimeoutSeconds = 90)
    {
        _connectionPool = connectionPool;
        ProviderType = type;
        _globalLimiter = globalLimiter;
        _bandwidthService = bandwidthService;
        _providerErrorService = providerErrorService;
        _providerIndex = providerIndex;
        _host = host ?? $"Provider {providerIndex}";
        _operationTimeoutSeconds = operationTimeoutSeconds;

        if (_providerIndex >= 0 && _bandwidthService != null && type != ProviderType.Disabled)
        {
            _latencyMonitorTimer = new Timer(CheckLatency, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    private void CheckLatency(object? state)
    {
        if (DateTimeOffset.UtcNow - _lastLatencyRecordTime <= TimeSpan.FromSeconds(45)) return;
        
        try
        {
            // We can't easily wait for this in a void timer callback, but we can fire and forget
            // However, we want to ensure we don't pile up checks if they are slow.
            // Since DateAsync uses connection pool, it will just wait/timeout if busy.
            // We use a separate async void wrapper or Task.Run to handle the async nature.
            Task.Run(async () =>
            {
                try
                {
                    // Use a short timeout for the ping
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    // Set context to Unknown so it doesn't look like a real user request, 
                    // or maybe "HealthCheck"? Let's stick to default/unknown for now or just minimal impact.
                    await DateAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "[MultiConnectionNntpClient] Latency check (ping) failed for provider {Host}", _host);
                }
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MultiConnectionNntpClient] Error initiating latency check");
        }
    }

    public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public Task<NntpStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.StatAsync(segmentId, ct), cancellationToken);
    }

    public Task<NntpDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.DateAsync(ct), cancellationToken);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.GetArticleHeadersAsync(segmentId, ct),
            cancellationToken);
    }

    public async Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        var stream = await RunStreamWithConnection(
            (connection, ct) => connection.GetSegmentStreamAsync(segmentId, includeHeaders, ct),
            cancellationToken, recordLatency: false).ConfigureAwait(false);

        if (_bandwidthService != null && _providerIndex >= 0)
        {
            var monitoringStream = new MonitoringStream(stream, bytes => _bandwidthService.RecordBytes(_providerIndex, bytes));
            return new YencHeaderStream(stream.Header, stream.ArticleHeaders, monitoringStream);
        }

        return stream;
    }

    public Task<YencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.GetSegmentYencHeaderAsync(segmentId, ct),
            cancellationToken, recordLatency: false);
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.GetFileSizeAsync(file, ct), cancellationToken, recordLatency: false);
    }

    public async Task WaitForReady(CancellationToken cancellationToken)
    {
        using var connectionLock =
            await _connectionPool.GetConnectionLockAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<NntpGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.GroupAsync(group, ct), cancellationToken);
    }

    public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.DownloadArticleBodyAsync(group, articleId, ct), cancellationToken, recordLatency: false);
    }

    private async Task<YencHeaderStream> RunStreamWithConnection
    (
        Func<INntpClient, CancellationToken, Task<YencHeaderStream>> task,
        CancellationToken cancellationToken,
        int retries = 5,
        bool recordLatency = true
    )
    {
        // Acquire global operation permit first (if global limiter is configured)
        GlobalOperationLimiter.OperationPermit? globalPermit = null;
        if (_globalLimiter != null)
        {
            var usageContext = cancellationToken.GetContext<ConnectionUsageContext>();
            var usageType = usageContext.UsageType;
            globalPermit = await _globalLimiter.AcquirePermitAsync(usageType, cancellationToken).ConfigureAwait(false);
        }

        // Create timeout cancellation token that will cancel after N seconds
        // IMPORTANT: Create this BEFORE acquiring connection lock so timeout includes lock wait time
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_operationTimeoutSeconds));

        // Propagate the connection usage context to the new linked token
        var originalUsageContext = cancellationToken.GetContext<ConnectionUsageContext>();
        IDisposable? timeoutContextScope = null;
        if (originalUsageContext.UsageType != ConnectionUsageType.Unknown)
        {
            timeoutContextScope = timeoutCts.Token.SetScopedContext(originalUsageContext);
        }

        ConnectionLock<INntpClient>? connectionLock = null;
        bool success = false;
        var startTime = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            connectionLock = await _connectionPool.GetConnectionLockAsync(timeoutCts.Token).ConfigureAwait(false);

            try
            {
                var operationStart = System.Diagnostics.Stopwatch.StartNew();

                YencHeaderStream stream;
                try
                {
                    // Pass the timeout token to the task - it will flow through to NNTP I/O operations
                    stream = await task(connectionLock.Connection, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    Serilog.Log.Debug("[MultiConnectionNntpClient] Stream operation timed out after {Timeout} seconds on provider {Host}", _operationTimeoutSeconds, _host);
                    throw new TimeoutException($"GetSegmentStream operation timed out after {_operationTimeoutSeconds} seconds on provider {_host}");
                }

                // Record latency metrics
                if (recordLatency && _bandwidthService != null && _providerIndex >= 0)
                {
                    _bandwidthService.RecordLatency(_providerIndex, (int)operationStart.ElapsedMilliseconds);
                    _lastLatencyRecordTime = DateTimeOffset.UtcNow;
                }

                // Create a callback stream that will handle the cleanup of the connection lock and global permit
                // when the stream is disposed.
                var wrappedStream = new DisposableCallbackStream(
                    stream,
                    onDisposeAsync: async () =>
                    {
                        try
                        {
                            // We assume connection is not "disposed" (replaced) because if it was, we would have caught exception below
                            // Wait for connection to be ready before returning to pool
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
                            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                            await connectionLock.Connection.WaitForReady(timeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            Serilog.Log.Warning("[MultiConnectionNntpClient] Connection cleanup timed out - forcing disposal to release resources.");
                            connectionLock.Replace();
                        }
                        catch (ObjectDisposedException)
                        {
                            // Connection was disposed (likely due to timeout/error during stream usage), so we must replace it.
                            connectionLock.Replace();
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "[MultiConnectionNntpClient] Error during connection cleanup");
                            connectionLock.Replace();
                        }
                        finally
                        {
                            connectionLock.Dispose();
                            globalPermit?.Dispose();
                        }
                    }
                );

                success = true;
                // Return a new YencHeaderStream that wraps our callback stream, preserving headers
                return new YencHeaderStream(stream.Header, stream.ArticleHeaders, wrappedStream);
            }
            catch (Exception ex) when (ex is NntpException or ObjectDisposedException or IOException or SocketException or TimeoutException)
            {
                // we want to replace the underlying connection in cases of NntpExceptions.
                connectionLock.Replace();
                connectionLock.Dispose();
                connectionLock = null;

                // and try again with a new connection (max 1 retry)
                if (retries > 0)
                {
                    // Release global permit before retry (we'll acquire a new one)
                    globalPermit?.Dispose();
                    globalPermit = null;
                    return await RunStreamWithConnection(task, cancellationToken, retries - 1, recordLatency).ConfigureAwait(false);
                }

                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var elapsedSeconds = startTime.Elapsed.TotalSeconds;
            Serilog.Log.Debug("[MultiConnectionNntpClient] [{Host}] Operation timed out after {ElapsedSeconds:F1}s (limit: {Timeout}s). This usually indicates slow Usenet server response or connection lock contention.", _host, elapsedSeconds, _operationTimeoutSeconds);
            throw new TimeoutException($"[{_host}] GetSegmentStream operation timed out after {elapsedSeconds:F1} seconds (limit: {_operationTimeoutSeconds}s)");
        }
        finally
        {
            _lastActivity = DateTimeOffset.UtcNow;
            timeoutContextScope?.Dispose(); // Dispose the scoped context if it was created
            // If we failed to create the stream (success == false), we must cleanup here.
            if (!success)
            {
                if (connectionLock != null)
                {
                    _ = connectionLock.Connection.WaitForReady(SigtermUtil.GetCancellationToken())
                        .ContinueWith(_ => connectionLock.Dispose());
                }
                globalPermit?.Dispose();
            }
        }
    }

    private async Task<T> RunWithConnection<T>
    (
        Func<INntpClient, CancellationToken, Task<T>> task,
        CancellationToken cancellationToken,
        int retries = 5,
        bool recordLatency = true
    )
    {
        // Acquire global operation permit first (if global limiter is configured)
        GlobalOperationLimiter.OperationPermit? globalPermit = null;
        if (_globalLimiter != null)
        {
            var usageContext = cancellationToken.GetContext<ConnectionUsageContext>();
            var usageType = usageContext.UsageType;
            globalPermit = await _globalLimiter.AcquirePermitAsync(usageType, cancellationToken).ConfigureAwait(false);
        }

        // Create timeout cancellation token that will cancel after N seconds
        // IMPORTANT: Create this BEFORE acquiring connection lock so timeout includes lock wait time
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_operationTimeoutSeconds));

        // Propagate the connection usage context to the new linked token
        var originalUsageContext = cancellationToken.GetContext<ConnectionUsageContext>();
        IDisposable? timeoutContextScope = null;
        if (originalUsageContext.UsageType != ConnectionUsageType.Unknown)
        {
            timeoutContextScope = timeoutCts.Token.SetScopedContext(originalUsageContext);
        }

        var startTime = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var connectionLock = await _connectionPool.GetConnectionLockAsync(timeoutCts.Token).ConfigureAwait(false);
            var isDisposed = false;
            try
            {
                var operationStart = System.Diagnostics.Stopwatch.StartNew();
                T result;
                try
                {
                    // Pass the timeout token to the task - it will flow through to NNTP I/O operations
                    result = await task(connectionLock.Connection, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    Serilog.Log.Debug("[MultiConnectionNntpClient] RunWithConnection: Operation timed out after {Timeout} seconds on provider {Host}", _operationTimeoutSeconds, _host);
                    throw new TimeoutException($"NNTP operation timed out after {_operationTimeoutSeconds} seconds on provider {_host}");
                }

                // Record latency metrics
                if (recordLatency && _bandwidthService != null && _providerIndex >= 0)
                {
                    _bandwidthService.RecordLatency(_providerIndex, (int)operationStart.ElapsedMilliseconds);
                    _lastLatencyRecordTime = DateTimeOffset.UtcNow;
                }

                return result;
            }
            catch (Exception ex) when (ex is NntpException or ObjectDisposedException or IOException or SocketException or TimeoutException)
            {
                // we want to replace the underlying connection in cases of NntpExceptions.
                connectionLock.Replace();
                connectionLock.Dispose();
                isDisposed = true;

                // and try again with a new connection (max 1 retry)
                if (retries > 0)
                {
                    // Release global permit before retry (we'll acquire a new one)
                    globalPermit?.Dispose();
                    globalPermit = null;
                    return await RunWithConnection<T>(task, cancellationToken, retries - 1, recordLatency).ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                // we only want to release the connection-lock once the underlying connection is ready again.
                //
                // ReSharper disable once MethodSupportsCancellation
                // we intentionally do not pass the cancellation token to ContinueWith,
                // since we want the continuation to always run.
                if (!isDisposed)
                    _ = connectionLock.Connection.WaitForReady(SigtermUtil.GetCancellationToken())
                        .ContinueWith(_ => connectionLock.Dispose());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var elapsedSeconds = startTime.Elapsed.TotalSeconds;
            Serilog.Log.Debug("[MultiConnectionNntpClient] [{Host}] Operation timed out after {ElapsedSeconds:F1}s (limit: {Timeout}s). This usually indicates slow Usenet server response or connection lock contention.", _host, elapsedSeconds, _operationTimeoutSeconds);
            throw new TimeoutException($"[{_host}] NNTP operation timed out after {elapsedSeconds:F1} seconds (limit: {_operationTimeoutSeconds}s)");
        }
        finally
        {
            _lastActivity = DateTimeOffset.UtcNow;
            timeoutContextScope?.Dispose(); // Dispose the scoped context if it was created
            // Release global permit
            globalPermit?.Dispose();
        }
    }

    public Task ForceReleaseConnections(ConnectionUsageType? type = null)
    {
        return _connectionPool.ForceReleaseConnections(type);
    }

    public void UpdateConnectionPool(ConnectionPool<INntpClient> connectionPool)
    {
        var oldConnectionPool = _connectionPool;
        _connectionPool = connectionPool;
        oldConnectionPool.Dispose();
    }

    public void Dispose()
    {
        _latencyMonitorTimer?.Dispose();
        _connectionPool.Dispose();
        GC.SuppressFinalize(this);
    }
}