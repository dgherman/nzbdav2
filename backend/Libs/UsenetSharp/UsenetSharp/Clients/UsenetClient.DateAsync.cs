using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            await WriteLineAsync("DATE".AsMemory(), _cts.Token);
            var response = await ReadLineAsync(_cts.Token);
            var responseCode = ParseResponseCode(response);

            // Response code 111 means success
            return new UsenetDateResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                DateTime = responseCode == (int)UsenetResponseType.DateAndTime
                    ? ParseNntpDateTime(response!)
                    : null
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private DateTimeOffset? ParseNntpDateTime(string response)
    {
        // DATE response format: "111 YYYYMMDDhhmmss"
        // Example: "111 20231215143022"
        if (string.IsNullOrEmpty(response))
        {
            return null;
        }

        var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var dateTimeString = parts[1];

        // Expected format: YYYYMMDDhhmmss (14 characters)
        if (dateTimeString.Length != 14)
        {
            return null;
        }

        try
        {
            var span = dateTimeString.AsSpan();
            var year = int.Parse(span.Slice(0, 4));
            var month = int.Parse(span.Slice(4, 2));
            var day = int.Parse(span.Slice(6, 2));
            var hour = int.Parse(span.Slice(8, 2));
            var minute = int.Parse(span.Slice(10, 2));
            var second = int.Parse(span.Slice(12, 2));

            // NNTP DATE returns UTC time
            var dateTime = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
            return dateTime;
        }
        catch
        {
            return null;
        }
    }
}