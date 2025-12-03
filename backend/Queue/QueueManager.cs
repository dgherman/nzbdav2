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
        await LockAsync(async () =>
        {
            var inProgressId = _inProgressQueueItem?.QueueItem?.Id;
            if (inProgressId is not null && queueItemIds.Contains(inProgressId.Value))
            {
                await _inProgressQueueItem!.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                await _inProgressQueueItem.ProcessingTask.ConfigureAwait(false);
                _inProgressQueueItem = null;
            }

            await dbClient.RemoveQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // get the next queue-item from the database
                QueueItem? queueItem = null;
                QueueNzbContents? queueNzbContents = null;

                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                    (queueItem, queueNzbContents) = await LockAsync(() => dbClient.GetTopQueueItem(ct)).ConfigureAwait(false);
                }

                if (queueItem is null || queueNzbContents is null)
                {
                    try
                    {
                        // if we're done with the queue, wait a minute before checking again.
                        // or wait until awoken by cancellation of _sleepingQueueToken
                        await Task.Delay(TimeSpan.FromMinutes(1), _sleepingQueueToken.Token).ConfigureAwait(false);
                    }
                    catch when (_sleepingQueueToken.IsCancellationRequested)
                    {
                        lock (_sleepingQueueLock)
                        {
                            _sleepingQueueToken.Dispose();
                            _sleepingQueueToken = new CancellationTokenSource();
                        }
                    }

                    continue;
                }

                // process the queue-item
                using var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await LockAsync(() =>
                {
                    _inProgressQueueItem = BeginProcessingQueueItem(
                        _scopeFactory, queueItem, queueNzbContents, queueItemCancellationTokenSource
                    );
                }).ConfigureAwait(false);
                await (_inProgressQueueItem?.ProcessingTask ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error($"An unexpected error occured while processing the queue: {e.Message}");
            }
            finally
            {
                await LockAsync(() => { _inProgressQueueItem = null; }).ConfigureAwait(false);
            }
        }
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

    private async Task LockAsync(Func<Task> actionAsync)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<T> LockAsync<T>(Func<Task<T>> actionAsync)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task LockAsync(Action action)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _semaphore.Release();
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