using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueManager : IDisposable
{
    private InProgressQueueItem? _inProgressQueueItem;

    private readonly UsenetStreamingClient _usenetClient;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly HealthCheckService _healthCheckService;
    private readonly IServiceScopeFactory _scopeFactory;

    private CancellationTokenSource _sleepingQueueToken = new();
    private readonly object _sleepingQueueLock = new();

    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        HealthCheckService healthCheckService,
        IServiceScopeFactory scopeFactory
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _websocketManager = websocketManager;
        _healthCheckService = healthCheckService;
        _scopeFactory = scopeFactory;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        _ = ProcessQueueAsync(_cancellationTokenSource.Token);
    }

    public (QueueItem? queueItem, int? progress) GetInProgressQueueItem()
    {
        return (_inProgressQueueItem?.QueueItem, _inProgressQueueItem?.ProgressPercentage);
    }

    public void AwakenQueue()
    {
        lock (_sleepingQueueLock)
        {
            _sleepingQueueToken.Cancel();
        }
    }

    public async Task RemoveQueueItemsAsync
    (
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        Log.Information("[QueueManager] RemoveQueueItemsAsync called for {Count} items: {ItemIds}",
            queueItemIds.Count, string.Join(", ", queueItemIds));

        // Check if we need to cancel the in-progress item
        Task? taskToWaitFor = null;
        await LockAsync(() =>
        {
            var inProgressId = _inProgressQueueItem?.QueueItem?.Id;
            if (inProgressId is not null && queueItemIds.Contains(inProgressId.Value))
            {
                Log.Warning("[QueueManager] Cancelling in-progress queue item: {Id}", inProgressId.Value);
                // Cancel the task but DON'T wait for it while holding the lock (deadlock!)
                _inProgressQueueItem!.CancellationTokenSource.Cancel();
                taskToWaitFor = _inProgressQueueItem.ProcessingTask;
            }
        }).ConfigureAwait(false);

        // Wait for the cancelled task to complete OUTSIDE the lock to avoid deadlock
        if (taskToWaitFor != null)
        {
            Log.Debug("[QueueManager] Waiting for cancelled task to complete...");
            try
            {
                await taskToWaitFor.ConfigureAwait(false);
                Log.Information("[QueueManager] Cancelled task completed successfully");
            }
            catch (Exception ex)
            {
                Log.Debug("[QueueManager] Cancelled task threw exception (expected): {Message}", ex.Message);
            }
        }

        // Now remove items from database
        Log.Debug("[QueueManager] Removing {Count} items from database", queueItemIds.Count);
        await dbClient.RemoveQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Information("[QueueManager] Successfully removed {Count} queue items", queueItemIds.Count);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        Log.Information("[QueueManager] ProcessQueueAsync loop started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // get the next queue-item from the database
                Log.Debug("[QueueManager] Fetching next queue item from database...");
                QueueItem? queueItem = null;
                QueueNzbContents? queueNzbContents = null;

                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                    (queueItem, queueNzbContents) = await LockAsync(() => dbClient.GetTopQueueItem(ct)).ConfigureAwait(false);
                }

                if (queueItem is null || queueNzbContents is null)
                {
                    Log.Debug("[QueueManager] No queue items available, sleeping for 1 minute...");
                    try
                    {
                        // if we're done with the queue, wait a minute before checking again.
                        // or wait until awoken by cancellation of _sleepingQueueToken
                        await Task.Delay(TimeSpan.FromMinutes(1), _sleepingQueueToken.Token).ConfigureAwait(false);
                    }
                    catch when (_sleepingQueueToken.IsCancellationRequested)
                    {
                        Log.Debug("[QueueManager] Queue awakened by cancellation token");
                        lock (_sleepingQueueLock)
                        {
                            _sleepingQueueToken.Dispose();
                            _sleepingQueueToken = new CancellationTokenSource();
                        }
                    }

                    continue;
                }

                Log.Information("[QueueManager] Starting to process queue item: {QueueItemId}, Name: {QueueItemJobName}, Priority: {Priority}, PauseUntil: {PauseUntil}",
                    queueItem.Id, queueItem.JobName, queueItem.Priority, queueItem.PauseUntil);
                // process the queue-item
                using var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await LockAsync(() =>
                {
                    Log.Debug("[QueueManager] Beginning processing task for queue item: {QueueItemId}", queueItem.Id);
                    _inProgressQueueItem = BeginProcessingQueueItem(
                        _scopeFactory, queueItem, queueNzbContents, queueItemCancellationTokenSource
                    );
                }).ConfigureAwait(false);

                Log.Debug("[QueueManager] Waiting for processing task to complete for queue item: {QueueItemId}", queueItem.Id);
                var processingTask = _inProgressQueueItem?.ProcessingTask ?? Task.CompletedTask;
                var startTime = DateTime.UtcNow;

                // Add timeout monitoring
                var completedTask = await Task.WhenAny(processingTask, Task.Delay(TimeSpan.FromMinutes(5), ct)).ConfigureAwait(false);
                if (completedTask != processingTask)
                {
                    Log.Warning("[QueueManager] Queue item {QueueItemId} ({JobName}) has not completed after 5 minutes, still waiting...",
                        queueItem.Id, queueItem.JobName);
                    await processingTask.ConfigureAwait(false);
                }

                var elapsed = DateTime.UtcNow - startTime;
                var taskStatus = processingTask.Status;
                Log.Information("[QueueManager] Completed processing queue item: {QueueItemId} ({JobName}). Status: {TaskStatus}, Elapsed: {ElapsedSeconds}s",
                    queueItem.Id, queueItem.JobName, taskStatus, elapsed.TotalSeconds);

                // Check if task faulted
                if (processingTask.IsFaulted && processingTask.Exception != null)
                {
                    Log.Error("[QueueManager] Processing task faulted with exception: {Exception}", processingTask.Exception.GetBaseException().Message);
                }
                else if (processingTask.IsCanceled)
                {
                    Log.Warning("[QueueManager] Processing task was canceled for queue item: {QueueItemId}", queueItem.Id);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "[QueueManager] An unexpected error occurred while processing the queue: {ErrorMessage}, StackTrace: {StackTrace}",
                    e.Message, e.StackTrace);
            }
            finally
            {
                Log.Debug("[QueueManager] Clearing in-progress queue item");
                await LockAsync(() => { _inProgressQueueItem = null; }).ConfigureAwait(false);
                Log.Debug("[QueueManager] Loop iteration complete, checking for next item...");
            }
        }
        Log.Debug("[QueueManager] ProcessQueueAsync main loop exited");
    }

    private InProgressQueueItem BeginProcessingQueueItem
    (
        IServiceScopeFactory scopeFactory,
        QueueItem queueItem,
        QueueNzbContents queueNzbContents,
        CancellationTokenSource cts
    )
    {
        var progressHook = new Progress<int>();
        var task = new QueueItemProcessor(
            queueItem, queueNzbContents, scopeFactory, _usenetClient, 
            _configManager, _websocketManager, _healthCheckService,
            progressHook, cts.Token
        ).ProcessAsync();
        var inProgressQueueItem = new InProgressQueueItem()
        {
            QueueItem = queueItem,
            ProcessingTask = task,
            ProgressPercentage = 0,
            CancellationTokenSource = cts
        };
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
        progressHook.ProgressChanged += (_, progress) =>
        {
            inProgressQueueItem.ProgressPercentage = progress;
            var message = $"{queueItem.Id}|{progress}";
            if (progress is 100 or 200) _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message);
            else debounce(() => _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message));
        };
        return inProgressQueueItem;
    }

    private async Task LockAsync(Func<Task> actionAsync, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        Log.Debug("[QueueManager] [{Caller}] Waiting for semaphore lock...", callerName);
        var startWait = DateTime.UtcNow;
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var waitTime = DateTime.UtcNow - startWait;
        if (waitTime.TotalSeconds > 1)
        {
            Log.Warning("[QueueManager] [{Caller}] Acquired semaphore lock after {WaitSeconds}s", callerName, waitTime.TotalSeconds);
        }
        else
        {
            Log.Debug("[QueueManager] [{Caller}] Acquired semaphore lock", callerName);
        }
        try
        {
            await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
            Log.Debug("[QueueManager] [{Caller}] Released semaphore lock", callerName);
        }
    }

    private async Task<T> LockAsync<T>(Func<Task<T>> actionAsync, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        Log.Debug("[QueueManager] [{Caller}] Waiting for semaphore lock...", callerName);
        var startWait = DateTime.UtcNow;
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var waitTime = DateTime.UtcNow - startWait;
        if (waitTime.TotalSeconds > 1)
        {
            Log.Warning("[QueueManager] [{Caller}] Acquired semaphore lock after {WaitSeconds}s", callerName, waitTime.TotalSeconds);
        }
        else
        {
            Log.Debug("[QueueManager] [{Caller}] Acquired semaphore lock", callerName);
        }
        try
        {
            return await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
            Log.Debug("[QueueManager] [{Caller}] Released semaphore lock", callerName);
        }
    }

    private async Task LockAsync(Action action, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        Log.Debug("[QueueManager] [{Caller}] Waiting for semaphore lock...", callerName);
        var startWait = DateTime.UtcNow;
        await _semaphore.WaitAsync().ConfigureAwait(false);
        var waitTime = DateTime.UtcNow - startWait;
        if (waitTime.TotalSeconds > 1)
        {
            Log.Warning("[QueueManager] [{Caller}] Acquired semaphore lock after {WaitSeconds}s", callerName, waitTime.TotalSeconds);
        }
        else
        {
            Log.Debug("[QueueManager] [{Caller}] Acquired semaphore lock", callerName);
        }
        try
        {
            action();
        }
        finally
        {
            _semaphore.Release();
            Log.Debug("[QueueManager] [{Caller}] Released semaphore lock", callerName);
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }

    private class InProgressQueueItem
    {
        public QueueItem QueueItem { get; init; }
        public int ProgressPercentage { get; set; }
        public Task ProcessingTask { get; init; }
        public CancellationTokenSource CancellationTokenSource { get; init; }
    }
}