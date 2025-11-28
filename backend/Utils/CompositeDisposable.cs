namespace NzbWebDAV.Utils;

/// <summary>
/// Disposes multiple IDisposable objects as a single unit.
/// Useful when multiple context scopes need to be kept alive together.
/// </summary>
public sealed class CompositeDisposable : IDisposable
{
    private readonly IDisposable[] _disposables;
    private bool _disposed;

    public CompositeDisposable(params IDisposable[] disposables)
    {
        _disposables = disposables ?? throw new ArgumentNullException(nameof(disposables));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var disposable in _disposables)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
                // Suppress exceptions to ensure all disposables are disposed
            }
        }
    }
}
