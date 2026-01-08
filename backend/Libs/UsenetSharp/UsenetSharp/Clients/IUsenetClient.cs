using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public interface IUsenetClient
{
    Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken);

    Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken);

    Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetBodyResponse> BodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetBodyResponse> BodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    Task<UsenetArticleResponse> ArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetArticleResponse> ArticleAsync
        (SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken);

    Task WaitForReadyAsync(
        CancellationToken cancellationToken);
}