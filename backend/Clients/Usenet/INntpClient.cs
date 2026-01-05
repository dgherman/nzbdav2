using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using Usenet.Nzb;
using Usenet.Yenc;

namespace NzbWebDAV.Clients.Usenet;

public interface INntpClient: IDisposable
{
    Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken);
    Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken);
    Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken);
    Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct);
    Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken);
    Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken);
    Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken);
    Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken);
    Task WaitForReady(CancellationToken cancellationToken);
    Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken cancellationToken);
    Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken cancellationToken);
}