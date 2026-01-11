using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using Usenet.Nzb;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient : INntpClient
{
    public IReadOnlyList<MultiConnectionNntpClient> Providers { get; }
    private readonly ProviderErrorService? _providerErrorService;
    private readonly NzbProviderAffinityService? _affinityService;

    public MultiProviderNntpClient(
        List<MultiConnectionNntpClient> providers,
        ProviderErrorService? providerErrorService = null,
        NzbProviderAffinityService? affinityService = null)
    {
        Providers = providers;
        _providerErrorService = providerErrorService;
        _affinityService = affinityService;
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
        return RunFromPoolWithBackup(
            connection => connection.StatAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "STAT");
    }

    public Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.DateAsync(cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetArticleHeadersAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "HEADER");
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "BODY");
    }

    public Task<YencHeaderStream> GetBalancedSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetBalancedProviders(cancellationToken),
            cancellationToken,
            segmentId,
            "BODY");
    }

    public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentYencHeaderAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            segmentId,
            "BODY");
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetFileSizeAsync(file, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            null,
            "STAT");
    }

    public Task WaitForReady(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GroupAsync(group, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken);
    }

    public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.DownloadArticleBodyAsync(group, articleId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider, cancellationToken),
            cancellationToken,
            null,
            "BODY");
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        IEnumerable<MultiConnectionNntpClient> orderedProviders,
        CancellationToken cancellationToken,
        string? segmentId = null,
        string operationName = "UNKNOWN"
    )
    {
        ExceptionDispatchInfo? lastException = null;
        var lastSuccessfulProviderContext = cancellationToken.GetContext<LastSuccessfulProviderContext>();
        var lastSuccessfulProvider = lastSuccessfulProviderContext?.Provider;
        T? result = default;
        int attempts = 0;

        foreach (var provider in orderedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ctx = cancellationToken.GetContext<ConnectionUsageContext>();
            if (ctx.DetailsObject != null)
            {
                if (provider.ProviderType != ProviderType.Pooled)
                {
                    ctx.DetailsObject.IsBackup = true;
                    ctx.DetailsObject.IsSecondary = false;
                }
                else if (attempts > 0)
                {
                    ctx.DetailsObject.IsSecondary = true;
                    ctx.DetailsObject.IsBackup = false;
                }
                else
                {
                    ctx.DetailsObject.IsSecondary = false;
                    ctx.DetailsObject.IsBackup = false;
                }
            }
            attempts++;

            if (lastException is not null && lastException.SourceException is not UsenetArticleNotFoundException)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug("Encountered error during NNTP Operation: {Message}. Trying another provider.", msg);
            }

            // Track timing for provider affinity
            var stopwatch = Stopwatch.StartNew();
            try
            {
                result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                if (result is UsenetStatResponse r && !r.ArticleExists)
                {
                    throw new UsenetArticleNotFoundException(r.ResponseMessage ?? segmentId ?? "Unknown");
                }

                // Record successful download for provider affinity
                if (_affinityService != null && operationName == "BODY")
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        // Estimate bytes from result
                        long bytes = 0;
                        if (result is YencHeaderStream stream) bytes = stream.Length;
                        else if (result is UsenetYencHeader header) bytes = header.PartSize;
                        else if (result is long l) bytes = l;

                        if (bytes == 0)
                        {
                            Log.Debug("[MultiProvider] Zero bytes recorded for BODY operation. ResultType={Type}, AffinityKey={Key}", result?.GetType().Name ?? "null", affinityKey);
                        }

                        _affinityService.RecordSuccess(affinityKey, provider.ProviderIndex, bytes, stopwatch.ElapsedMilliseconds);
                    }
                }

                if (lastSuccessfulProviderContext is not null && lastSuccessfulProvider != provider)
                    lastSuccessfulProviderContext.Provider = provider;
                return result;
            }
            catch (UsenetArticleNotFoundException e)
            {
                stopwatch.Stop();

                // Explicitly caught to record it
                RecordMissingArticle(provider.ProviderIndex, e.SegmentId, cancellationToken, operationName);

                // Record failure for provider affinity
                if (_affinityService != null)
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        _affinityService.RecordFailure(affinityKey, provider.ProviderIndex);
                    }
                }

                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch (Exception e) when (e is not OperationCanceledException and not TaskCanceledException)
            {
                stopwatch.Stop();

                // Record failure for provider affinity
                if (_affinityService != null)
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        _affinityService.RecordFailure(affinityKey, provider.ProviderIndex);
                    }
                }

                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch (OperationCanceledException ex)
            {
                stopwatch.Stop();

                // Record timeout/cancellation as failure for provider affinity (only if it's a real timeout, not parent cancellation)
                if (_affinityService != null && !cancellationToken.IsCancellationRequested)
                {
                    var affinityKey = ctx.AffinityKey;
                    if (!string.IsNullOrEmpty(affinityKey))
                    {
                        _affinityService.RecordFailure(affinityKey, provider.ProviderIndex);
                    }
                }

                // If parent cancellation is requested, stop everything immediately
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Otherwise, this was a provider-specific timeout (e.g. from GetDynamicTimeout in MultiConnectionNntpClient)
                // We treat this as a transient failure and try the next provider.
                var elapsed = stopwatch.Elapsed.TotalSeconds;
                Log.Debug("Operation timed out on provider {Host} after {Elapsed:F2}s. Trying another provider.", provider.Host, elapsed);
                lastException = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (result is UsenetStatResponse)
            return result;

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private void RecordMissingArticle(int providerIndex, string segmentId, CancellationToken ct, string operation)
    {
        var context = ct.GetContext<ConnectionUsageContext>();
        var filename = context.DetailsObject?.Text ?? context.Details ?? "Unknown";

        var providerHost = providerIndex >= 0 && providerIndex < Providers.Count
            ? Providers[providerIndex].Host
            : $"Provider {providerIndex}";

        if (_providerErrorService == null) return;
        _providerErrorService.RecordError(providerIndex, filename, segmentId ?? "", "Article not found", context.IsImported, operation);
    }

    private IEnumerable<MultiConnectionNntpClient> GetOrderedProviders(MultiConnectionNntpClient? preferredProvider, CancellationToken cancellationToken)
    {
        // Check for NZB-level provider affinity with epsilon-greedy exploration
        MultiConnectionNntpClient? affinityProvider = null;
        if (_affinityService != null)
        {
            var context = cancellationToken.GetContext<ConnectionUsageContext>();
            var affinityKey = context.AffinityKey;

            if (!string.IsNullOrEmpty(affinityKey))
            {
                // Only log affinity decisions for buffer streaming operations
                var logDecision = context.UsageType == ConnectionUsageType.BufferedStreaming;
                var preferredIndex = _affinityService.GetPreferredProvider(affinityKey, Providers.Count, logDecision);
                if (preferredIndex.HasValue && preferredIndex.Value >= 0 && preferredIndex.Value < Providers.Count)
                {
                    affinityProvider = Providers[preferredIndex.Value];
                }
            }
        }

        return Providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => x.IdleConnections)
            .ThenByDescending(x => x.RemainingSemaphoreSlots)
            .Prepend(preferredProvider)     // Stream-level stickiness
            .Prepend(affinityProvider)      // NZB-level affinity (HIGHEST priority - overrides stream stickiness)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct();
    }

    private IEnumerable<MultiConnectionNntpClient> GetBalancedProviders(CancellationToken cancellationToken = default)
    {
        // Balanced strategy for BufferedStream with NZB-level affinity support:
        // 1. Prioritize affinity provider (learned performance)
        // 2. Then Pooled providers sorted by available connections and latency
        // 3. Fallback to Backups

        // Check for NZB-level provider affinity
        MultiConnectionNntpClient? affinityProvider = null;
        if (_affinityService != null)
        {
            var context = cancellationToken.GetContext<ConnectionUsageContext>();
            var affinityKey = context.AffinityKey;

            if (!string.IsNullOrEmpty(affinityKey))
            {
                // Log affinity decisions for buffer streaming
                var logDecision = context.UsageType == ConnectionUsageType.BufferedStreaming;
                var preferredIndex = _affinityService.GetPreferredProvider(affinityKey, Providers.Count, logDecision);
                if (preferredIndex.HasValue && preferredIndex.Value >= 0 && preferredIndex.Value < Providers.Count)
                {
                    affinityProvider = Providers[preferredIndex.Value];
                }
            }
        }

        var pooled = Providers
            .Where(x => x.ProviderType == ProviderType.Pooled)
            .OrderByDescending(x => x.AvailableConnections > 0)
            .ThenBy(x => x.AverageLatency)
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();

        var others = Providers
            .Where(x => x.ProviderType != ProviderType.Pooled && x.ProviderType != ProviderType.Disabled)
            .OrderBy(x => x.ProviderType) // Backup vs BackupOnly
            .ThenByDescending(x => x.IdleConnections);

        return pooled.Concat(others)
            .Prepend(affinityProvider)  // NZB-level affinity takes priority
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct();
    }

    public Task ForceReleaseConnections(ConnectionUsageType? type = null)
    {
        var tasks = Providers.Select(p => p.ForceReleaseConnections(type));
        return Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        foreach (var provider in Providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
