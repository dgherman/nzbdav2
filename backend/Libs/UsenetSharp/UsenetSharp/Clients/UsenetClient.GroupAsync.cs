using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            await WriteLineAsync($"GROUP {group}".AsMemory(), _cts.Token);
            var response = await ReadLineAsync(_cts.Token);
            var responseCode = ParseResponseCode(response);

            if (responseCode == 211 && !string.IsNullOrEmpty(response))
            {
                var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Expected: 211 count first last group
                if (parts.Length >= 5)
                {
                    long.TryParse(parts[1], out var count);
                    long.TryParse(parts[2], out var first);
                    long.TryParse(parts[3], out var last);
                    var groupName = parts[4];

                    return new UsenetGroupResponse
                    {
                        ResponseCode = responseCode,
                        ResponseMessage = response,
                        Count = count,
                        First = first,
                        Last = last,
                        Group = groupName
                    };
                }
            }

            return new UsenetGroupResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response ?? string.Empty
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
