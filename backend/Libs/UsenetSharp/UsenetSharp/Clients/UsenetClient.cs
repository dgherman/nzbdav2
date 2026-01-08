namespace UsenetSharp.Clients;

public partial class UsenetClient : IUsenetClient, IDisposable
{
    private bool _disposed;

    public async Task WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        _commandLock.Release();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            CleanupConnection();
        }

        _disposed = true;
    }
}