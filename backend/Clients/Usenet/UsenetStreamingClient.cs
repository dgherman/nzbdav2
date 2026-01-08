using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Websocket;
using Serilog;
using UsenetSharp.Models;
using Usenet.Nzb;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient
{
    private readonly CachingNntpClient _client;
    private readonly WebsocketManager _websocketManager;
    private readonly ConfigManager _configManager;
    private readonly BandwidthService _bandwidthService;
    private readonly ProviderErrorService _providerErrorService;
    private readonly NzbProviderAffinityService _affinityService;
    private ConnectionPoolStats? _connectionPoolStats;

    // Track recent GetFileSizeAsync operation times for dynamic timeout calculation
    private readonly Queue<double> _recentFileSizeOperationTimes = new();
    private readonly object _fileSizeTimingLock = new();
    private const int MaxTrackedOperations = 20;

    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        BandwidthService bandwidthService,
        ProviderErrorService providerErrorService,
        NzbProviderAffinityService affinityService)
    {
        // initialize private members
        _websocketManager = websocketManager;
        _configManager = configManager;
        _bandwidthService = bandwidthService;
        _providerErrorService = providerErrorService;
        _affinityService = affinityService;

        // get connection settings from config-manager
        var providerConfig = configManager.GetUsenetProviderConfig();

        // initialize the nntp-client
        var multiProviderClient = CreateMultiProviderClient(providerConfig);
        var cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 8192 });
        _client = new CachingNntpClient(multiProviderClient, cache);

        // when config changes, update the connection-pool
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.TryGetValue("usenet.providers", out var rawConfig) && 
                !configEventArgs.ChangedConfig.ContainsKey("usenet.operation-timeout")) return;

            // update the connection-pool according to the new config
            var newProviderConfig = configManager.GetUsenetProviderConfig();
            var newMultiProviderClient = CreateMultiProviderClient(newProviderConfig!);
            _client.UpdateUnderlyingClient(newMultiProviderClient);
        };
    }

    public async Task<long[]> AnalyzeNzbAsync(string[] segmentIds, int concurrency, IProgress<int>? progress, CancellationToken ct, bool useSmartAnalysis = true)
    {
        // Copy context from parent token
        using var childCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var _1 = childCt.Token.SetScopedContext(ct.GetContext<LastSuccessfulProviderContext>());
        using var _2 = childCt.Token.SetScopedContext(ct.GetContext<ConnectionUsageContext>());
        var token = childCt.Token;
        var usageContext = token.GetContext<ConnectionUsageContext>();
        var timeoutSeconds = _configManager.GetUsenetOperationTimeout();

        // Optimization: Smart Analysis for uniform segment sizes
        if (useSmartAnalysis && segmentIds.Length > 2)
        {
            try
            {
                using var fastCheckCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                fastCheckCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var _ = fastCheckCts.Token.SetScopedContext(usageContext);

                // Check first, second (to confirm uniformity), and last segment
                var first = await _client.GetSegmentYencHeaderAsync(segmentIds[0], fastCheckCts.Token).ConfigureAwait(false);
                var second = await _client.GetSegmentYencHeaderAsync(segmentIds[1], fastCheckCts.Token).ConfigureAwait(false);
                var last = await _client.GetSegmentYencHeaderAsync(segmentIds[^1], fastCheckCts.Token).ConfigureAwait(false);

                // If first two segments match in size
                if (first.PartSize == second.PartSize)
                {
                    // Calculate expected total size based on uniformity assumption
                    // Total = (Size * (N-1)) + LastSize
                    long expectedTotal = (first.PartSize * (segmentIds.Length - 1)) + last.PartSize;

                    // Calculate actual total size from last segment's YEnc header
                    // Last segment header contains "begin" offset and its own size
                    long actualTotal = last.PartOffset + last.PartSize;

                    if (expectedTotal == actualTotal)
                    {
                        Serilog.Log.Debug("[UsenetStreamingClient] Smart Analysis: Uniform segments detected ({Size} bytes). Skipping full scan for {Count} segments.", first.PartSize, segmentIds.Length);
                        
                        var fastSizes = new long[segmentIds.Length];
                        Array.Fill(fastSizes, first.PartSize);
                        fastSizes[^1] = last.PartSize;
                        
                        progress?.Report(segmentIds.Length);
                        return fastSizes;
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[UsenetStreamingClient] Smart Analysis failed/skipped. Falling back to full scan.");
            }
        }

        var sizes = new long[segmentIds.Length];
        var tasks = segmentIds
            .Select(async (id, index) =>
            {
                using var segmentCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                segmentCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var _ = segmentCts.Token.SetScopedContext(usageContext);

                var header = await _client.GetSegmentYencHeaderAsync(id, segmentCts.Token).ConfigureAwait(false);
                sizes[index] = header.PartSize;
                return index;
            })
            .WithConcurrencyAsync(concurrency);

        var processed = 0;
        try
        {            await foreach (var _ in tasks.ConfigureAwait(false))
            {
                progress?.Report(++processed);
            }
        }
        catch (Exception)
        {
            await childCt.CancelAsync().ConfigureAwait(false);
            throw;
        }

        return sizes;
    }
    public async Task<long[]?> CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        bool useHead = true
    )
    {
        if (useHead)
        {
            // Health checks should always perform full HEAD scan, never Smart Analysis
            return await AnalyzeNzbAsync(segmentIds.ToArray(), concurrency, progress, cancellationToken, useSmartAnalysis: false).ConfigureAwait(false);
        }

        // No need to copy ReservedPooledConnectionsContext - operation limits handle this now
        using var childCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var _1 = childCt.Token.SetScopedContext(cancellationToken.GetContext<LastSuccessfulProviderContext>());
        using var _2 = childCt.Token.SetScopedContext(cancellationToken.GetContext<ConnectionUsageContext>());
        var token = childCt.Token;
        var usageContext = token.GetContext<ConnectionUsageContext>();
        var timeoutSeconds = _configManager.GetUsenetOperationTimeout();

        var tasks = segmentIds
            .Select(async x =>
            {
                using var segmentCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                segmentCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var _ = segmentCts.Token.SetScopedContext(usageContext);
                try
                {
                    // STAT: Faster, but can be inaccurate (only checks metadata existence)
                    var result = await _client.StatAsync(x, segmentCts.Token).ConfigureAwait(false);
                    if (!result.ArticleExists)
                        throw new UsenetArticleNotFoundException(x);

                    return x;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && segmentCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Stat timed out for segment {x}");
                }
            })
            .WithConcurrencyAsync(concurrency);

        var processed = 0;
        try
        {
            await foreach (var segmentId in tasks.ConfigureAwait(false))
            {
                progress?.Report(++processed);
            }
        }
        catch (Exception)
        {
            await childCt.CancelAsync().ConfigureAwait(false);
            throw;
        }

        return null;
    }

    public async Task<NzbFileStream> GetFileStream(NzbFile nzbFile, int concurrentConnections, CancellationToken ct)
    {
        var usageContext = new ConnectionUsageContext(ConnectionUsageType.Streaming);
        using var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var _ = linkedCt.Token.SetScopedContext(usageContext);

        var segmentIds = nzbFile.GetSegmentIds();
        var fileSize = await _client.GetFileSizeAsync(nzbFile, linkedCt.Token).ConfigureAwait(false);
        var bufferSize = _configManager.GetStreamBufferSize();
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections, usageContext, bufferSize: bufferSize);
    }

    public NzbFileStream GetFileStream(NzbFile nzbFile, long fileSize, int concurrentConnections, long[]? segmentSizes = null)
    {
        var bufferSize = _configManager.GetStreamBufferSize();
        var usageContext = new ConnectionUsageContext(ConnectionUsageType.Streaming);
        return new NzbFileStream(nzbFile.GetSegmentIds(), fileSize, _client, concurrentConnections, usageContext, bufferSize: bufferSize, segmentSizes: segmentSizes);
    }

    public NzbFileStream GetFileStream(string[] segmentIds, long fileSize, int concurrentConnections, ConnectionUsageContext? usageContext = null, bool useBufferedStreaming = true, int? bufferSize = null, long[]? segmentSizes = null)
    {
        // Use config value if not specified
        var actualBufferSize = bufferSize ?? _configManager.GetStreamBufferSize();
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections, usageContext, useBufferedStreaming, actualBufferSize, segmentSizes);
    }

    public NzbFileStream GetFastFileStream(string[] segmentIds, long fileSize, int concurrentConnections, ConnectionUsageContext? usageContext = null)
    {
        // Create uninitialized segment sizes so NzbFileStream trusts the fileSize
        var segmentSizes = new long[segmentIds.Length];
        if (segmentIds.Length > 0)
        {
            var avgSize = fileSize / segmentIds.Length;
            Array.Fill(segmentSizes, avgSize);
            segmentSizes[^1] = fileSize - (avgSize * (segmentIds.Length - 1));
        }
        
        return GetFileStream(segmentIds, fileSize, concurrentConnections, usageContext, useBufferedStreaming: false, segmentSizes: segmentSizes);
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
    {
        return _client.GetSegmentStreamAsync(segmentId, includeHeaders, ct);
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return _client.GetFileSizeAsync(file, cancellationToken);
    }

    private int GetDynamicFileSizeTimeout(int providerCount, int defaultTimeoutPerProvider)
    {
        lock (_fileSizeTimingLock)
        {
            // If we don't have enough history, use conservative default
            if (_recentFileSizeOperationTimes.Count < 3)
            {
                // Default: allow each provider full timeout + buffer
                return (providerCount * defaultTimeoutPerProvider) + 60;
            }

            // Calculate average time from recent operations
            var avgSeconds = _recentFileSizeOperationTimes.Average();

            // Apply 4x safety factor
            var dynamicTimeout = (int)(avgSeconds * 4);

            // Clamp between reasonable bounds:
            // Min: 60s (even fast operations need some buffer)
            // Max: (providers * timeout) + 60s (allow trying all providers)
            var maxTimeout = (providerCount * defaultTimeoutPerProvider) + 60;
            var clampedTimeout = Math.Clamp(dynamicTimeout, 60, maxTimeout);

            return clampedTimeout;
        }
    }

    private void RecordFileSizeOperationTime(double elapsedSeconds)
    {
        lock (_fileSizeTimingLock)
        {
            _recentFileSizeOperationTimes.Enqueue(elapsedSeconds);

            // Keep only last N operations
            while (_recentFileSizeOperationTimes.Count > MaxTrackedOperations)
            {
                _recentFileSizeOperationTimes.Dequeue();
            }
        }
    }

    public async Task<Dictionary<NzbFile, long>> GetFileSizesBatchAsync(
        IEnumerable<NzbFile> files,
        int concurrentConnections,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<NzbFile, long>();
        var filesToFetch = files.Where(f => f.Segments.Count > 0).ToList();

        if (filesToFetch.Count == 0)
            return results;

        var usageContext = cancellationToken.GetContext<ConnectionUsageContext>();

        // Calculate dynamic timeout based on historical performance
        var providerConfig = _configManager.GetUsenetProviderConfig();
        var providerCount = providerConfig.Providers.Count;
        var timeoutPerProvider = _configManager.GetUsenetOperationTimeout();
        var adaptiveTimeoutSeconds = GetDynamicFileSizeTimeout(providerCount, timeoutPerProvider);

        var operationStartTime = Stopwatch.StartNew();

        var tasks = filesToFetch
            .Select(async file =>
            {
                // CRITICAL: Pass original cancellationToken (NOT a timeout-wrapped token)
                // This allows RunFromPoolWithBackup to try all providers when one times out
                // Each provider has its own timeout via GetDynamicTimeout in MultiConnectionNntpClient
                using var _ = cancellationToken.SetScopedContext(usageContext);

                // Create absolute timeout that will fail the operation if exceeded
                // This timeout is adaptive: avg * 4x, allowing all providers to be tried
                using var absoluteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                absoluteCts.CancelAfter(TimeSpan.FromSeconds(adaptiveTimeoutSeconds));

                try
                {
                    var size = await _client.GetFileSizeAsync(file, absoluteCts.Token).ConfigureAwait(false);
                    return (file, size);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && absoluteCts.IsCancellationRequested)
                {
                    // Adaptive timeout exceeded after trying providers
                    throw new TimeoutException($"GetFileSizeAsync timed out for file {file.FileName} after {adaptiveTimeoutSeconds}s (adaptive timeout: avg * 4x)");
                }
            })
            .WithConcurrencyAsync(concurrentConnections);

        await foreach (var (file, size) in tasks.ConfigureAwait(false))
        {
            results[file] = size;
        }

        operationStartTime.Stop();

        // Record successful operation time for future dynamic timeout calculations
        if (filesToFetch.Count > 0)
        {
            // Normalize by number of files to get per-file average
            var perFileTime = operationStartTime.Elapsed.TotalSeconds / filesToFetch.Count;
            RecordFileSizeOperationTime(perFileTime);
        }

        return results;
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return _client.GetArticleHeadersAsync(segmentId, cancellationToken);
    }

    private ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        ExtendedSemaphoreSlim pooledSemaphore,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        ConnectionPoolStats connectionPoolStats,
        int providerIndex,
        string host
    )
    {
        // Create connection pool (uses global semaphore for all providers)
        var pool = new ConnectionPool<INntpClient>(maxConnections, pooledSemaphore, connectionFactory, poolName: host);
        pool.OnConnectionPoolChanged += onConnectionPoolChanged;
        connectionPoolStats.RegisterConnectionPool(providerIndex, pool);
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(pool, args);

        return pool;
    }

    private MultiProviderNntpClient CreateMultiProviderClient(UsenetProviderConfig providerConfig)
    {
        var connectionPoolStats = new ConnectionPoolStats(providerConfig, _websocketManager);
        _connectionPoolStats = connectionPoolStats;
        var totalPooledConnectionCount = providerConfig.TotalPooledConnections;
        var pooledSemaphore = new ExtendedSemaphoreSlim(totalPooledConnectionCount, totalPooledConnectionCount);
        
        var operationTimeout = _configManager.GetUsenetOperationTimeout();

        // Create ONE global operation limiter shared across ALL providers
        var maxQueueConnections = _configManager.GetMaxQueueConnections();
        var maxHealthCheckConnections = _configManager.GetMaxRepairConnections();
        var globalLimiter = new GlobalOperationLimiter(
            maxQueueConnections,
            maxHealthCheckConnections,
            totalPooledConnectionCount,
            _configManager
        );

        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats,
                index,
                pooledSemaphore,
                globalLimiter,
                _providerErrorService,
                operationTimeout
            ))
            .ToList();
        return new MultiProviderNntpClient(providerClients, _providerErrorService, _affinityService);
    }

    private MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        ConnectionPoolStats connectionPoolStats,
        int providerIndex,
        ExtendedSemaphoreSlim pooledSemaphore,
        GlobalOperationLimiter globalLimiter,
        ProviderErrorService providerErrorService,
        int operationTimeout
    )
    {
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
            pooledSemaphore: pooledSemaphore,
            connectionFactory: ct => CreateNewConnection(connectionDetails, _bandwidthService, providerIndex, ct),
            onConnectionPoolChanged: connectionPoolStats.GetOnConnectionPoolChanged(providerIndex),
            connectionPoolStats: connectionPoolStats,
            providerIndex: providerIndex,
            host: connectionDetails.Host
        );
        return new MultiConnectionNntpClient(
            connectionPool,
            connectionDetails.Type,
            globalLimiter,
            _bandwidthService,
            providerErrorService,
            providerIndex,
            connectionDetails.Host,
            operationTimeout,
            _configManager
        );
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        BandwidthService bandwidthService,
        int providerIndex,
        CancellationToken cancellationToken
    )
    {
        var connection = new ThreadSafeNntpClient(bandwidthService, providerIndex);
        var host = connectionDetails.Host;
        var port = connectionDetails.Port;
        var useSsl = connectionDetails.UseSsl;
        var user = connectionDetails.User;
        var pass = connectionDetails.Pass;

        try
        {
            if (!await connection.ConnectAsync(host, port, useSsl, cancellationToken).ConfigureAwait(false))
                throw new CouldNotConnectToUsenetException($"Could not connect to usenet host ({host}:{port}). Check connection settings.");
            if (!await connection.AuthenticateAsync(user, pass, cancellationToken).ConfigureAwait(false))
                throw new CouldNotLoginToUsenetException($"Could not login to usenet host ({host}:{port}). Check username and password.");
            return connection;
        }
        catch (OperationCanceledException)
        {
            connection.Dispose();
            throw new OperationCanceledException($"Connection to usenet host ({host}:{port}) timed out or was canceled.");
        }
        catch (Exception)
        {
            connection.Dispose();
            throw;
        }
    }

    public Task ResetConnections(ConnectionUsageType? type = null)
    {
        if (_client.InnerClient is MultiProviderNntpClient multiProvider)
        {
            return multiProvider.ForceReleaseConnections(type);
        }
        return Task.CompletedTask;
    }

    public Dictionary<int, List<ConnectionUsageContext>> GetActiveConnectionsByProvider()
    {
        return _connectionPoolStats?.GetActiveConnectionsByProvider() ?? new Dictionary<int, List<ConnectionUsageContext>>();
    }
}