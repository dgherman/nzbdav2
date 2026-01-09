using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        // Clean up any existing connection
        CleanupConnection();
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _tcpClient = new TcpClient();

            // Phase 6: Optimize TCP socket for high-throughput Usenet streaming
            _tcpClient.ReceiveBufferSize = 524288;  // 512KB receive buffer (2x increase for better throughput)
            _tcpClient.SendBufferSize = 131072;     // 128KB send buffer (aligned to NZBGet chunk size)
            _tcpClient.NoDelay = false;             // Enable Nagle's algorithm (optimize for throughput over latency)

            await _tcpClient.ConnectAsync(host, port, cancellationToken);
            _stream = _tcpClient.GetStream();

            if (useSsl)
            {
                var sslStream = new SslStream(_stream, false);
                await sslStream.AuthenticateAsClientAsync(host, null,
                    System.Security.Authentication.SslProtocols.Tls12 |
                    System.Security.Authentication.SslProtocols.Tls13, true);
                _stream = sslStream;
            }

            // Phase 6: Align buffers to 128KB (NZBGet chunk size)
            _reader = new StreamReader(_stream, Encoding.Latin1, false, 131072);  // 128KB buffer
            _writer = new StreamWriter(_stream, Encoding.Latin1, 131072) { AutoFlush = true };

            // Read the server response
            var response = await ReadLineAsync(_cts.Token);
            var responseCode = ParseResponseCode(response);

            // NNTP servers typically respond with "200" or "201" for successful connection
            if (responseCode != (int)UsenetResponseType.ServerReadyPostingAllowed &&
                responseCode != (int)UsenetResponseType.ServerReadyNoPostingAllowed)
                throw new UsenetConnectionException(response!) { ResponseCode = responseCode };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}