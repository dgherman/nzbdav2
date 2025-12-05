using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using Serilog;
using Usenet.Nntp.Responses;
using Usenet.Nzb;
using Usenet.Yenc;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(List<MultiConnectionNntpClient> providers) : INntpClient
{
    public IReadOnlyList<MultiConnectionNntpClient> Providers => providers;

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
            cancellationToken);
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
            cancellationToken);
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken);
    }

    public Task<YencHeaderStream> GetBalancedSegmentStreamAsync(string segmentId, bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken),
            GetBalancedProviders(),
            cancellationToken);
    }

    public Task<YencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            connection => connection.GetSegmentYencHeaderAsync(segmentId, cancellationToken),
            GetOrderedProviders(cancellationToken.GetContext<LastSuccessfulProviderContext>()?.Provider),
            cancellationToken);
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
        CancellationToken cancellationToken
    )
    {
        ExceptionDispatchInfo? lastException = null;
        var lastSuccessfulProviderContext = cancellationToken.GetContext<LastSuccessfulProviderContext>();
        var lastSuccessfulProvider = lastSuccessfulProviderContext?.Provider;
        T? result = default;
        foreach (var provider in orderedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (lastException is not null && lastException.SourceException is not UsenetArticleNotFoundException)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            try
            {
                result = await task.Invoke(provider).ConfigureAwait(false);
                if (result is NntpStatResponse r && r.ResponseType != NntpStatResponseType.ArticleExists)
                    throw new UsenetArticleNotFoundException(r.MessageId.Value);

                if (lastSuccessfulProviderContext is not null && lastSuccessfulProvider != provider)
                    lastSuccessfulProviderContext.Provider = provider;
                return result;
            }
            catch (Exception e) when (e is not OperationCanceledException and not TaskCanceledException)
            {
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        if (result is NntpStatResponse)
            return result;

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private IEnumerable<MultiConnectionNntpClient> GetOrderedProviders(MultiConnectionNntpClient? preferredProvider)
    {
        return providers
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

        var pooled = providers
            .Where(x => x.ProviderType == ProviderType.Pooled)
            .OrderByDescending(x => x.AvailableConnections > 0)
            .ThenBy(x => x.AverageLatency)
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();

        var others = providers
            .Where(x => x.ProviderType != ProviderType.Pooled && x.ProviderType != ProviderType.Disabled)
            .OrderBy(x => x.ProviderType) // Backup vs BackupOnly
            .ThenByDescending(x => x.IdleConnections);

        return pooled.Concat(others);
    }

    public void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}