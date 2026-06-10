// backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs
namespace NzbWebDAV.Clients.Usenet.Concurrency;

public class PrioritizedSemaphore : IDisposable
{
    private readonly LinkedList<TaskCompletionSource<bool>> _highPriorityWaiters = [];
    private readonly LinkedList<TaskCompletionSource<bool>> _lowPriorityWaiters = [];
    private SemaphorePriorityOdds _priorityOdds;
    private int _maxAllowed;
    private int _enteredCount;
    private bool _disposed = false;
    private readonly Lock _lock = new();
    private int _accumulatedOdds;

    public PrioritizedSemaphore(int initialAllowed, int maxAllowed, SemaphorePriorityOdds? priorityOdds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialAllowed);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAllowed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialAllowed, maxAllowed);
        _priorityOdds = priorityOdds ?? new SemaphorePriorityOdds { HighPriorityOdds = 100 };
        _enteredCount = maxAllowed - initialAllowed;
        _maxAllowed = maxAllowed;
    }

    public int CurrentCount
    {
        get { lock (_lock) { return _maxAllowed - _enteredCount; } }
    }

    public Task WaitAsync(SemaphorePriority priority, CancellationToken cancellationToken = default)
    {
        // Bail out immediately if the token is already cancelled — avoids entering
        // the lock at all (and prevents a synchronous Register callback inside Lock
        // which would throw LockRecursionException on .NET 9+).
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        Task task;
        LinkedList<TaskCompletionSource<bool>>? capturedQueue = null;
        LinkedListNode<TaskCompletionSource<bool>>? capturedNode = null;
        TaskCompletionSource<bool>? capturedTcs = null;

        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PrioritizedSemaphore));

            if (_enteredCount < _maxAllowed)
            {
                _enteredCount++;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = priority == SemaphorePriority.High ? _highPriorityWaiters : _lowPriorityWaiters;
            var node = queue.AddLast(tcs);

            // Capture the TCS task and locals needed for cancellation registration
            // OUTSIDE the lock, so the Register callback won't nest-lock a
            // non-reentrant System.Threading.Lock (.NET 9+).
            task = tcs.Task;

            if (cancellationToken.CanBeCanceled)
            {
                capturedQueue = queue;
                capturedNode = node;
                capturedTcs = tcs;
            }
        }

        // Register cancellation AFTER releasing the lock. If the token fires
        // between lock exit and Register, the waiter stays in the queue
        // harmlessly — Release() will skip canceled TCS entries. If the token
        // fires after Register, the callback safely removes the node.
        if (cancellationToken.CanBeCanceled && capturedQueue is not null)
        {
            var ct = cancellationToken;
            var registration = ct.Register(() =>
            {
                var removed = false;
                lock (_lock)
                {
                    try
                    {
                        capturedQueue.Remove(capturedNode!);
                        removed = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // Node already removed by Release() — that's fine.
                    }
                }

                if (removed)
                    capturedTcs!.TrySetCanceled(ct);
            });

            task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return task;
    }

    public void Release()
    {
        TaskCompletionSource<bool>? toRelease;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PrioritizedSemaphore));

            if (_enteredCount > _maxAllowed)
            {
                toRelease = null;
            }
            else if (_highPriorityWaiters.Count == 0)
            {
                toRelease = Release(_lowPriorityWaiters);
            }
            else if (_lowPriorityWaiters.Count == 0)
            {
                toRelease = Release(_highPriorityWaiters);
            }
            else
            {
                _accumulatedOdds += _priorityOdds.LowPriorityOdds;
                var (one, two) = (_highPriorityWaiters, _lowPriorityWaiters);
                if (_accumulatedOdds >= 100)
                {
                    (one, two) = (two, one);
                    _accumulatedOdds -= 100;
                }

                toRelease = Release(one) ?? Release(two);
            }

            if (toRelease == null)
            {
                _enteredCount--;
                if (_enteredCount < 0)
                {
                    throw new InvalidOperationException("The semaphore cannot be further released.");
                }

                return;
            }
        }

        toRelease.TrySetResult(true);
    }

    private static TaskCompletionSource<bool>? Release(LinkedList<TaskCompletionSource<bool>> queue)
    {
        while (queue.Count > 0)
        {
            var node = queue.First!;
            queue.RemoveFirst();

            if (!node.Value.Task.IsCanceled)
            {
                return node.Value;
            }
        }

        return null;
    }

    public void UpdateMaxAllowed(int newMaxAllowed)
    {
        lock (_lock)
        {
            _maxAllowed = newMaxAllowed;
        }
    }

    public void UpdatePriorityOdds(SemaphorePriorityOdds newPriorityOdds)
    {
        lock (_lock)
        {
            _priorityOdds = newPriorityOdds;
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource<bool>> waitersToCancel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waitersToCancel = _highPriorityWaiters.Concat(_lowPriorityWaiters).ToList();
            _highPriorityWaiters.Clear();
            _lowPriorityWaiters.Clear();
        }

        foreach (var tcs in waitersToCancel)
            tcs.TrySetException(new ObjectDisposedException(nameof(PrioritizedSemaphore)));
    }
}
