using System.IO;
using System.Net.Sockets;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using Usenet.Nzb;

namespace NzbWebDAV.Clients.Usenet;

public class MultiConnectionNntpClient : INntpClient
{
    public ProviderType ProviderType { get; }
    public int LiveConnections => _connectionPool.LiveConnections;
    public int IdleConnections => _connectionPool.IdleConnections;
    public int ActiveConnections => _connectionPool.ActiveConnections;
    public int AvailableConnections => _connectionPool.AvailableConnections;
    public int RemainingSemaphoreSlots => _connectionPool.RemainingSemaphoreSlots;
    public ConnectionPool<INntpClient> ConnectionPool => _connectionPool;

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
    private readonly ComponentLogger? _logger;

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
        int operationTimeoutSeconds = 90,
        ConfigManager? configManager = null)
    {
        _connectionPool = connectionPool;
        ProviderType = type;
        _globalLimiter = globalLimiter;
        _bandwidthService = bandwidthService;
        _providerErrorService = providerErrorService;
        _providerIndex = providerIndex;
        _host = host ?? $"Provider {providerIndex}";
        _operationTimeoutSeconds = operationTimeoutSeconds;
        _logger = configManager != null ? new ComponentLogger(LogComponents.Usenet, configManager) : null;

        if (_providerIndex >= 0 && _bandwidthService != null && type != ProviderType.Disabled)
        {
            _latencyMonitorTimer = new Timer(CheckLatency, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    private int GetDynamicTimeout()
    {
        var latency = _bandwidthService?.GetAverageLatency(_providerIndex) ?? 0;
        if (latency <= 0) return _operationTimeoutSeconds * 1000;

        // Formula: Latency * 4, clamped between MinTimeout and ConfiguredTimeout
        // Minimum of 45s (increased from 15s) because:
        // - Connection pool contention can add significant wait time
        // - Health checks with many concurrent segments need headroom
        // - Occasional slow segments shouldn't fail the entire operation
        const int MinTimeoutMs = 45000;
        var dynamic = latency * 4;
        return (int)Math.Clamp(dynamic, MinTimeoutMs, _operationTimeoutSeconds * 1000);
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
                    // Set context to Analysis so it's clear it's a provider health check/ping
                    using var _ = cts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Analysis, "Latency Check"));
                    await DateAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Debug("Latency check (ping) failed for provider {Host}: {Error}", _host, ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Error initiating latency check");
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

    public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunWithConnection((connection, ct) => connection.StatAsync(segmentId, ct), cancellationToken);
    }

    public Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
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

    public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
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

    public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
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
        var currentTimeoutMs = GetDynamicTimeout();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(currentTimeoutMs));

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
                    _logger?.Debug("Stream operation timed out after {Timeout:F1} seconds on provider {Host}", currentTimeoutMs / 1000.0, _host);
                    throw new TimeoutException($"GetSegmentStream operation timed out after {currentTimeoutMs / 1000.0:F1} seconds on provider {_host}");
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
                                                // Wait for connection to be ready before returning to pool
                                                // Use a short timeout (500ms) to allow quick draining of small/nearly-complete segments.
                                                // If it takes longer, we kill the connection to ensure UI responsiveness during seeking.
                                                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
                                                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                                                await connectionLock.Connection.WaitForReady(timeoutCts.Token).ConfigureAwait(false);
                                            }
                                            catch (IOException ex)
                                            {
                                                // IOException during WaitForReady indicates a dirty connection (timeout or partial drain)
                                                _logger?.Debug("Connection cleanup failed (IOException): {Message}. Connection will be discarded/replaced.", ex.Message);
                                                connectionLock.Replace();
                                            }
                                            catch (OperationCanceledException)
                                            {
                                                _logger?.Debug("Connection cleanup timed out - forcing disposal to release resources.");
                                                connectionLock.Replace();
                                            }
                                            catch (ObjectDisposedException)
                                            {
                                                // Connection was disposed (likely due to timeout/error during stream usage), so we must replace it.
                                                connectionLock.Replace();
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger?.Warning(ex, "An unexpected error occurred during connection cleanup");
                                                connectionLock.Replace();
                                            }
                                            finally
                                            {
                                                connectionLock.Dispose();
                                                globalPermit?.Dispose();
                                            }
                                        }                );

                success = true;
                // Return a new YencHeaderStream that wraps our callback stream, preserving headers
                return new YencHeaderStream(stream.Header, stream.ArticleHeaders, wrappedStream);
            }
            catch (Exception ex) when (ex is UsenetException or UsenetNotConnectedException or ObjectDisposedException or IOException or SocketException or TimeoutException or RetryableDownloadException)
            {
                // we want to replace the underlying connection in cases of NntpExceptions or login failures.
                connectionLock.Replace();
                connectionLock.Dispose();
                connectionLock = null;

                // and try again with a new connection (max 1 retry)
                if (retries > 0)
                {
                    _logger?.Debug("[RunStreamWithConnection] Retrying operation. Retries left: {Retries}, Provider: {Host}, Exception: {Exception}", retries, _host, ex.GetType().Name);
                    
                    // Release permit before retrying to prevent deadlock
                    globalPermit?.Dispose();
                    globalPermit = null;

                    // Add small delay before retry
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    return await RunStreamWithConnection(task, cancellationToken, retries - 1, recordLatency).ConfigureAwait(false);
                }

                _logger?.Warning("[RunStreamWithConnection] Final retry failed. Retries: {Retries}, Provider: {Host}, Exception: {Exception}", retries, _host, ex.GetType().Name);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var elapsedSeconds = startTime.Elapsed.TotalSeconds;
            _logger?.Debug("[{Host}] Operation timed out after {ElapsedSeconds:F1}s (limit: {Timeout:F1}s). This usually indicates slow Usenet server response or connection lock contention.", _host, elapsedSeconds, currentTimeoutMs / 1000.0);
            throw new TimeoutException($"[{_host}] GetSegmentStream operation timed out after {elapsedSeconds:F1} seconds (limit: {currentTimeoutMs / 1000.0:F1}s)");
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
                                    .ContinueWith(t =>
                                    {
                                        if (t.IsFaulted)
                                        {
                                            _logger?.Debug("Background connection cleanup failed: {Message}", t.Exception?.InnerException?.Message);
                                            connectionLock.Replace();
                                        }
                                        connectionLock.Dispose();
                                    });
                            }
                        }
                        // Always dispose permit in finally block of the current recursive call
                        globalPermit?.Dispose();
                    }    }

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
        var currentTimeoutMs = GetDynamicTimeout();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(currentTimeoutMs));

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
                    _logger?.Debug("RunWithConnection: Operation timed out after {Timeout:F1} seconds on provider {Host}", currentTimeoutMs / 1000.0, _host);
                    throw new TimeoutException($"NNTP operation timed out after {currentTimeoutMs / 1000.0:F1} seconds on provider {_host}");
                }

                // Record latency metrics
                if (recordLatency && _bandwidthService != null && _providerIndex >= 0)
                {
                    _bandwidthService.RecordLatency(_providerIndex, (int)operationStart.ElapsedMilliseconds);
                    _lastLatencyRecordTime = DateTimeOffset.UtcNow;
                }

                return result;
            }
            catch (Exception ex) when (ex is UsenetException or UsenetNotConnectedException or ObjectDisposedException or IOException or SocketException or TimeoutException or RetryableDownloadException)
            {
                // we want to replace the underlying connection in cases of NntpExceptions or login failures.
                connectionLock.Replace();
                connectionLock.Dispose();
                isDisposed = true;

                // and try again with a new connection (max 1 retry)
                if (retries > 0)
                {
                    _logger?.Debug("[RunWithConnection] Retrying operation. Retries left: {Retries}, Provider: {Host}, Exception: {Exception}", retries, _host, ex.GetType().Name);
                    
                    // Release permit before retrying to prevent deadlock (holding permit while waiting for new one)
                    globalPermit?.Dispose();
                    globalPermit = null;

                    // Add small delay before retry
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    return await RunWithConnection<T>(task, cancellationToken, retries - 1, recordLatency).ConfigureAwait(false);
                }

                _logger?.Warning("[RunWithConnection] Final retry failed. Retries: {Retries}, Provider: {Host}, Exception: {Exception}", retries, _host, ex.GetType().Name);
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
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                _logger?.Debug("Background connection cleanup failed: {Message}", t.Exception?.InnerException?.Message);
                                connectionLock.Replace();
                            }
                            connectionLock.Dispose();
                        });
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var elapsedSeconds = startTime.Elapsed.TotalSeconds;
            _logger?.Debug("[{Host}] Operation timed out after {ElapsedSeconds:F1}s (limit: {Timeout:F1}s). This usually indicates slow Usenet server response or connection lock contention.", _host, elapsedSeconds, currentTimeoutMs / 1000.0);
            throw new TimeoutException($"[{_host}] NNTP operation timed out after {elapsedSeconds:F1} seconds (limit: {currentTimeoutMs / 1000.0:F1}s)");
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