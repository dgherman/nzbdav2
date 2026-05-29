namespace UsenetSharp.Concurrency;

public class AsyncSemaphore : IDisposable
{
    private readonly LinkedList<TaskCompletionSource<bool>> _waiters = new();
    private int _currentCount;
    private bool _disposed = false;
    private readonly object _lock = new();

    public AsyncSemaphore(int initialCount)
    {
        if (initialCount < 0) throw new ArgumentOutOfRangeException(nameof(initialCount));
        _currentCount = initialCount;
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        // Propagate pre-cancelled tokens immediately without entering the lock.
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            if (_currentCount > 0)
            {
                _currentCount--;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = _waiters.AddLast(tcs);

            // Return the TCS task, then register cancellation *outside* the lock.
            // This matches the pattern in ExtendedSemaphoreSlim and prevents the
            // callback from executing under the lock.
            var task = tcs.Task;

            if (cancellationToken.CanBeCanceled)
            {
                var capturedNode = node;
                var capturedTcs = tcs;
                var capturedCt = cancellationToken;

                var registration = capturedCt.Register(() =>
                {
                    bool removed = false;
                    lock (_lock)
                    {
                        try
                        {
                            _waiters.Remove(capturedNode);
                            removed = true;
                        }
                        catch (InvalidOperationException)
                        {
                            // Node already removed by Release() — that's fine.
                        }
                    }

                    if (removed)
                        capturedTcs.TrySetCanceled(capturedCt);
                });

                task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            return task;
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? toRelease = null;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            while (_waiters.Count > 0)
            {
                var node = _waiters.First!;
                _waiters.RemoveFirst();

                // Skip canceled tasks
                if (!node.Value.Task.IsCanceled)
                {
                    toRelease = node.Value;
                    break;
                }
            }

            if (toRelease == null)
            {
                _currentCount++;
                return;
            }
        }

        toRelease.TrySetResult(true);
    }

    public void Dispose()
    {
        List<TaskCompletionSource<bool>> waitersToCancel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waitersToCancel = new List<TaskCompletionSource<bool>>(_waiters);
            _waiters.Clear();
        }

        foreach (var tcs in waitersToCancel)
            tcs.TrySetException(new ObjectDisposedException(nameof(AsyncSemaphore)));
    }
}