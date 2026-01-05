// ReSharper disable InconsistentNaming

namespace NzbWebDAV.Extensions;

public static class IEnumerableTaskExtensions
{
    /// <summary>
    /// Executes tasks with specified concurrency and enumerates results as they come in
    /// </summary>
    /// <param name="tasks">The tasks to execute</param>
    /// <param name="concurrency">The max concurrency</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <typeparam name="T">The resulting type of each task</typeparam>
    /// <returns>An IAsyncEnumerable that enumerates task results as they come in</returns>
    public static IEnumerable<Task<T>> WithConcurrency<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency
    ) where T : IDisposable
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        if (concurrency == 1)
        {
            foreach (var task in tasks) yield return task;
            yield break;
        }

        var isFirst = true;
        var runningTasks = new Queue<Task<T>>();
        try
        {
            foreach (var task in tasks)
            {
                if (isFirst)
                {
                    // help with time-to-first-byte
                    yield return task;
                    isFirst = false;
                    continue;
                }

                runningTasks.Enqueue(task);
                if (runningTasks.Count < concurrency) continue;
                yield return runningTasks.Dequeue();
            }

            while (runningTasks.Count > 0)
                yield return runningTasks.Dequeue();
        }
        finally
        {
            while (runningTasks.Count > 0)
            {
                runningTasks.Dequeue().ContinueWith(x =>
                {
                    if (x.Status == TaskStatus.RanToCompletion)
                        x.Result.Dispose();
                });
            }
        }
    }

    public static async IAsyncEnumerable<T> WithConcurrencyAsync<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        var runningTasks = new HashSet<Task<T>>();
        var totalStarted = 0;
        var totalCompleted = 0;

        try
        {
            foreach (var task in tasks)
            {
                runningTasks.Add(task);
                totalStarted++;

                if (runningTasks.Count < concurrency) continue;
                var completedTask = await Task.WhenAny(runningTasks).ConfigureAwait(false);
                runningTasks.Remove(completedTask);
                totalCompleted++;
                yield return await completedTask.ConfigureAwait(false);
            }

            while (runningTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(runningTasks).ConfigureAwait(false);
                runningTasks.Remove(completedTask);
                totalCompleted++;
                yield return await completedTask.ConfigureAwait(false);
            }
        }
        finally
        {
            // CRITICAL: Wait for all running tasks to complete to ensure proper resource cleanup
            // If we don't do this, tasks that are still running will leak connections
            if (runningTasks.Count > 0)
            {
                Serilog.Log.Warning("[WithConcurrencyAsync] Exception occurred with {RunningTasks} tasks still running. Waiting for cleanup...",
                    runningTasks.Count);

                // Wait for all remaining tasks to complete (successfully or with exception)
                // This ensures that all streams are properly disposed via their 'await using' blocks
                // Cast to base Task type to use SuppressThrowing (not supported on Task<T>)
                try
                {
                    await ((Task)Task.WhenAll(runningTasks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                catch
                {
                    // Suppressed - we just need cleanup to complete
                }

                Serilog.Log.Debug("[WithConcurrencyAsync] All {RunningTasks} tasks have completed cleanup",
                    runningTasks.Count);
            }
        }
    }
}