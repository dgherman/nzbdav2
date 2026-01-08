using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            // Send AUTHINFO USER command
            await WriteLineAsync($"AUTHINFO USER {user}".AsMemory(), _cts.Token);
            var userResponse = await ReadLineAsync(_cts.Token);
            var userResponseCode = ParseResponseCode(userResponse);

            // Password required
            if (userResponseCode == (int)UsenetResponseType.PasswordRequired)
            {
                // Send AUTHINFO PASS command
                await WriteLineAsync($"AUTHINFO PASS {pass}".AsMemory(), _cts.Token);
                var passResponse = await ReadLineAsync(_cts.Token);
                var passResponseCode = ParseResponseCode(passResponse);

                return new UsenetResponse()
                {
                    ResponseCode = passResponseCode,
                    ResponseMessage = passResponse!,
                };
            }

            return new UsenetResponse()
            {
                ResponseCode = userResponseCode,
                ResponseMessage = userResponse!,
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}