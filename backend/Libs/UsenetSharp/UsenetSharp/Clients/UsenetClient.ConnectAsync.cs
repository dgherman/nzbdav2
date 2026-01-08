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

            // Optimize TCP socket for high-throughput Usenet streaming
            _tcpClient.ReceiveBufferSize = 262144;  // 256KB receive buffer for better network throughput
            _tcpClient.SendBufferSize = 65536;      // 64KB send buffer
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

            // Use Latin1 encoding to preserve exact byte values 0-255 for yEnc-encoded content
            // Use 64KB buffer for StreamReader to match TcpClient buffer optimizations
            _reader = new StreamReader(_stream, Encoding.Latin1, false, 65536);
            _writer = new StreamWriter(_stream, Encoding.Latin1, 65536) { AutoFlush = true };

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