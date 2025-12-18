using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using Serilog;
using Usenet.Nntp.Responses;
using Usenet.Nzb;
using Usenet.Yenc;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient : INntpClient
{
    public IReadOnlyList<MultiConnectionNntpClient> Providers { get; }
    private readonly ProviderErrorService? _providerErrorService;

    public MultiProviderNntpClient(List<MultiConnectionNntpClient> providers, ProviderErrorService? providerErrorService = null)
    {
        Providers = providers;
        _providerErrorService = providerErrorService;
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
        return RunFromPoolWithBackup(
            connection => connection.StatAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken,
            segmentId);
    }

    public Task<NntpDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.DateAsync(cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetArticleHeadersAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken,
            segmentId);
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken,
            segmentId);
    }

    public Task<YencHeaderStream> GetBalancedSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetBalancedProviders(),
            cancellationToken,
            segmentId);
    }

    public Task<YencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentYencHeaderAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken,
            segmentId);
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetFileSizeAsync(file, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken);
    }

    public Task WaitForReady(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<NntpGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GroupAsync(group, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken);
    }

    public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.DownloadArticleBodyAsync(group, articleId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken);
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        IEnumerable<MultiConnectionNntpClient> orderedProviders,
        CancellationToken cancellationToken,
        string? segmentId = null
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
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            try
            {
                result = await task.Invoke(provider).ConfigureAwait(false);
                if (result is NntpStatResponse r && r.ResponseType != NntpStatResponseType.ArticleExists)
                {
                    throw new UsenetArticleNotFoundException(r.MessageId.Value);
                }

                if (lastSuccessfulProviderContext is not null && lastSuccessfulProvider != provider)
                    lastSuccessfulProviderContext.Provider = provider;
                return result;
            }
            catch (UsenetArticleNotFoundException e)
            {
                // Explicitly caught to record it
                RecordMissingArticle(provider.ProviderIndex, e.SegmentId, cancellationToken);
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch (Exception e) when (e is not OperationCanceledException and not TaskCanceledException)
            {
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch (OperationCanceledException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Operation timed out on provider {provider.Host} (Segment: {segmentId ?? "N/A"})", ex);
                }
                throw;
            }
        }

        if (result is NntpStatResponse)
            return result;

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private void RecordMissingArticle(int providerIndex, string segmentId, CancellationToken ct)
    {
        var context = ct.GetContext<ConnectionUsageContext>();
        var filename = context.DetailsObject?.Text ?? context.Details ?? "Unknown";

        var providerHost = providerIndex >= 0 && providerIndex < Providers.Count
            ? Providers[providerIndex].Host
            : $"Provider {providerIndex}";

        if (_providerErrorService == null) return;
        _providerErrorService.RecordError(providerIndex, filename, segmentId ?? "", "Article not found", context.IsImported);
    }

    private IEnumerable<MultiConnectionNntpClient> GetOrderedProviders(MultiConnectionNntpClient? preferredProvider)
    {
        return Providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => x.IdleConnections)
            .ThenByDescending(x => x.RemainingSemaphoreSlots)
            .Prepend(preferredProvider)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct();
    }

    private IEnumerable<MultiConnectionNntpClient> GetBalancedProviders()
    {
        // Balanced strategy for BufferedStream:
        // 1. Prioritize Pooled providers.
        // 2. Within Pooled, prioritize those with available connections.
        // 3. Then prefer lower latency.
        // 4. Fallback to Backups.

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

        return pooled.Concat(others);
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