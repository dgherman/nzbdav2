using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            // Send STAT command with message-id
            await WriteLineAsync($"STAT <{segmentId}>".AsMemory(), _cts.Token);
            var response = await ReadLineAsync(_cts.Token);
            var responseCode = ParseResponseCode(response);

            return new UsenetStatResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                ArticleExists = responseCode == (int)UsenetResponseType.ArticleExists,
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}