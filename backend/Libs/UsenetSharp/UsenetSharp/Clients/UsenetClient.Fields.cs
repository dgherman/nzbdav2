using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using UsenetSharp.Concurrency;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private AsyncSemaphore _commandLock = new(1);
    private CancellationTokenSource _cts = new();
    private volatile ExceptionDispatchInfo? _backgroundException = null;
}