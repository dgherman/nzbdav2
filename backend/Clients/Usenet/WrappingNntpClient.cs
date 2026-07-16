using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using Usenet.Nzb;

namespace NzbWebDAV.Clients.Usenet;

public abstract class WrappingNntpClient(INntpClient client) : INntpClient
{
    protected INntpClient Client = client;
    public INntpClient InnerClient => Client;

    public virtual Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        return Client.ConnectAsync(host, port, useSsl, cancellationToken);
    }

    public virtual Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        return Client.AuthenticateAsync(user, pass, cancellationToken);
    }

    public virtual Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return Client.StatAsync(segmentId, cancellationToken);
    }

    public virtual Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return Client.DateAsync(cancellationToken);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return Client.GetArticleHeadersAsync(segmentId, cancellationToken);
    }

    public virtual Task<YencHeaderStream> GetSegmentStreamAsync
    (
        string segmentId,
        bool includeHeaders,
        CancellationToken cancellationToken
    )
    {
        return Client.GetSegmentStreamAsync(segmentId, includeHeaders, cancellationToken);
    }

    public virtual Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return Client.GetSegmentYencHeaderAsync(segmentId, cancellationToken);
    }

    public virtual Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return Client.GetFileSizeAsync(file, cancellationToken);
    }

    public virtual Task WaitForReady(CancellationToken cancellationToken)
    {
        return Client.WaitForReady(cancellationToken);
    }

    public virtual Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
    {
        return Client.GroupAsync(group, cancellationToken);
    }

    public virtual Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken cancellationToken)
    {
        return Client.DownloadArticleBodyAsync(group, articleId, cancellationToken);
    }

    // How long a replaced client is left alive for in-flight operations to finish before it is disposed.
    private static readonly TimeSpan ReplacedClientDrainPeriod = TimeSpan.FromMinutes(2);

    public void UpdateUnderlyingClient(INntpClient client)
    {
        var oldClient = Client;
        Client = client;

        // Do not dispose inline. Streams that are mid-read still hold a reference to the old client and
        // its connection pools; disposing it under them turns a provider-settings save into
        // ObjectDisposedExceptions on active playback. New work already goes to the new client, so give
        // the old one a drain window and then let it go.
        _ = Task.Delay(ReplacedClientDrainPeriod).ContinueWith(_ =>
        {
            try
            {
                oldClient.Dispose();
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[WrappingNntpClient] Error disposing replaced client after drain period.");
            }
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        Client.Dispose();
        GC.SuppressFinalize(this);
    }
}