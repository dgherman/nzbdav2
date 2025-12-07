using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;
using Usenet.Nzb;

namespace NzbWebDAV.Queue;

public class QueueItemProcessor(
    QueueItem queueItem,
    QueueNzbContents queueNzbContents,
    IServiceScopeFactory scopeFactory,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    HealthCheckService healthCheckService,
    IProgress<int> progress,
    CancellationToken ct
)
{
    public async Task ProcessAsync()
    {
        Log.Information($"[Queue] Starting processing for {queueItem.JobName} ({queueItem.Id})");
        // initialize
        var startTime = DateTime.Now;
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");

        // process the job
        try
        {
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (e.GetBaseException() is OperationCanceledException or TaskCanceledException)
        {
            Log.Information($"Processing of queue item `{queueItem.JobName}` was cancelled.");
            // No need to clear change tracker as we use short-lived contexts now
        }

        // when a retryable error is encountered
        // let's not remove the item from the queue
        // to give it a chance to retry. Simply
        // log the error and retry in a minute.
        catch (Exception e) when (e.IsRetryableDownloadException())
        {
            try
            {
                Log.Error($"Failed to process job, `{queueItem.JobName}` -- {e.Message}");
                using var scope = scopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                
                // We need to attach queueItem to the new context because it was tracked by the old (now gone) context?
                // Actually queueItem object comes from QueueManager loop context which is disposed?
                // No, QueueManager loop keeps context alive.
                // BUT QueueItemProcessor now doesn't have that context.
                // So we must attach it or fetch it again.
                
                // Fetching fresh is safer.
                var item = await dbClient.Ctx.QueueItems.FirstOrDefaultAsync(x => x.Id == queueItem.Id, ct);
                if (item != null)
                {
                    item.PauseUntil = DateTime.Now.AddMinutes(1);
                    await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
            }
            catch (Exception ex)
            {
                Log.Error(ex, ex.Message);
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception e)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                await MarkQueueItemCompleted(dbClient, startTime, error: e.Message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(e, ex.Message);
            }
        }
    }

    private async Task ProcessQueueItemAsync(DateTime startTime)
    {
        DavItem? existingMountFolder = null;
        string duplicateNzbBehavior = "ignore"; // default

        // Scope 1: Initial DB checks
        using (var scope = scopeFactory.CreateScope())
        {
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            existingMountFolder = await GetMountFolder(dbClient).ConfigureAwait(false);
            duplicateNzbBehavior = configManager.GetDuplicateNzbBehavior();

            // if the mount folder already exists and setting is `marked-failed`
            // then immediately mark the job as failed.
            var isDuplicateNzb = existingMountFolder is not null;
            if (isDuplicateNzb && duplicateNzbBehavior == "mark-failed")
            {
                const string error = "Duplicate nzb: the download folder for this nzb already exists.";
                await MarkQueueItemCompleted(dbClient, startTime, error, () => Task.FromResult(existingMountFolder)).ConfigureAwait(false);
                return;
            }
        }

        // GlobalOperationLimiter now handles all connection limits - no need for reserved connections
        var providerConfig = configManager.GetUsenetProviderConfig();
        var concurrency = configManager.GetMaxQueueConnections();
        Log.Information($"[Queue] Processing '{queueItem.JobName}': TotalConnections={providerConfig.TotalPooledConnections}, MaxQueueConnections={concurrency}");
        using var _1 = ct.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Queue, queueItem.JobName));

        // read the nzb document
        var documentBytes = Encoding.UTF8.GetBytes(queueNzbContents.NzbContents);
        using var stream = new MemoryStream(documentBytes);
        var nzb = await NzbDocument.LoadAsync(stream).ConfigureAwait(false);
        var archivePassword = nzb.MetaData.GetValueOrDefault("password")?.FirstOrDefault();
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/nzbdav-dev/nzbdav/issues/101
        var articlesToPrecheck = nzbFiles.SelectMany(x => x.Segments).Select(x => x.MessageId.Value);
        healthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);

        // step 1 -- get name and size of each nzb file
        Log.Information($"[Queue] Step 1: Fetching first segments for {nzbFiles.Count} files with concurrency={concurrency}");
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var segments = await FetchFirstSegmentsStep.FetchFirstSegments(
            nzbFiles, usenetClient, configManager, ct, part1Progress).ConfigureAwait(false);
        var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
            segments, usenetClient, ct).ConfigureAwait(false);
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);

        // step 1b -- batch fetch file sizes for files without Par2 descriptors
        var filesWithoutSize = fileInfos.Where(f => f.FileSize == null).Select(f => f.NzbFile).ToList();
        if (filesWithoutSize.Count > 0)
        {
            Log.Information($"[Queue] Step 1b: Batch fetching file sizes for {filesWithoutSize.Count} files (Par2 provided {fileInfos.Count - filesWithoutSize.Count} sizes)");
            var fileSizes = await usenetClient.GetFileSizesBatchAsync(filesWithoutSize, concurrency, ct).ConfigureAwait(false);
            foreach (var fileInfo in fileInfos.Where(f => f.FileSize == null))
            {
                if (fileSizes.TryGetValue(fileInfo.NzbFile, out var size))
                {
                    fileInfo.FileSize = size;
                }
            }
        }
        else
        {
            Log.Information($"[Queue] Step 1b: Skipped - Par2 file provided all {fileInfos.Count} file sizes");
        }

        // step 2 -- perform file processing
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword).ToList();

        // Reduce concurrency to prevent connection pool exhaustion when processing archives
        // Each RAR can use up to 5 connections, so with 145 total connections:
        // - MaxQueueConnections reserves 30 permits for queue operations
        // - But physical connection pool is shared, so limit concurrent file processing
        // - Safe limit: totalConnections / maxConnectionsPerFile = 145 / 5 = 29
        var fileConcurrency = Math.Min(concurrency, providerConfig.TotalPooledConnections / 5);

        Log.Information($"[Queue] Step 2: Processing {fileProcessors.Count} file groups with concurrency={fileConcurrency}");
        var part2Progress = progress
            .Offset(50)
            .Scale(50, 100)
            .ToPercentage(fileProcessors.Count);
        var fileProcessingResultsAll = await fileProcessors
            .Select(x => x!.ProcessAsync())
            .WithConcurrencyAsync(fileConcurrency)
            .GetAllAsync(ct, part2Progress).ConfigureAwait(false);
        var fileProcessingResults = fileProcessingResultsAll
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        // step 3 -- Optionally check full article existence
        var checkedFullHealth = false;
        if (configManager.IsEnsureArticleExistenceEnabled())
        {
            var articlesToCheck = fileInfos
                .Where(x => x.IsRar || FilenameUtil.IsImportantFileType(x.FileName))
                .SelectMany(x => x.NzbFile.GetSegmentIds())
                .ToList();
            Log.Information($"[Queue] Step 3: Checking {articlesToCheck.Count} article segments with concurrency={concurrency}");
            var part3Progress = progress
                .Offset(100)
                .ToPercentage(articlesToCheck.Count);
            await usenetClient.CheckAllSegmentsAsync(articlesToCheck, concurrency, part3Progress, ct).ConfigureAwait(false);
            checkedFullHealth = true;
        }

        // Scope 2: Final DB update
        using (var scope = scopeFactory.CreateScope())
        {
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            // update the database
            await MarkQueueItemCompleted(dbClient, startTime, error: null, async () =>
            {
                var categoryFolder = await GetOrCreateCategoryFolder(dbClient).ConfigureAwait(false);
                var mountFolder = await CreateMountFolder(dbClient, categoryFolder, existingMountFolder, duplicateNzbBehavior).ConfigureAwait(false);
                new RarAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
                new FileAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
                new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
                new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);

                // post-processing
                new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
                new BlacklistedExtensionPostProcessor(configManager, dbClient).RemoveBlacklistedExtensions();

                // validate video files found
                if (configManager.IsEnsureImportableVideoEnabled())
                    new EnsureImportableVideoValidator(dbClient).ThrowIfValidationFails();

                // create strm files, if necessary
                if (configManager.GetImportStrategy() == "strm")
                    await new CreateStrmFilesPostProcessor(configManager, dbClient).CreateStrmFilesAsync().ConfigureAwait(false);

                return mountFolder;
            }).ConfigureAwait(false);
        }
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword
    )
    {
        var maxConnections = configManager.GetMaxQueueConnections();
        var groups = fileInfos
            .DistinctBy(x => x.FileName)
            .GroupBy(GetGroup);

        // Calculate adaptive concurrency per RAR to avoid connection pool exhaustion
        // WithConcurrencyAsync will run multiple RARs in parallel, so we need to limit per-RAR connections
        var rarCount = groups.Count(g => g.Key == "rar");
        var connectionsPerRar = rarCount > 0
            ? Math.Max(1, Math.Min(5, maxConnections / Math.Max(1, rarCount / 3)))
            : 1;
        Log.Debug($"[Queue] Adaptive RAR concurrency: {connectionsPerRar} connections per RAR ({rarCount} RAR files, {maxConnections} total connections)");

        foreach (var group in groups)
        {
            Log.Debug($"[Queue] Processing group '{group.Key}' with {group.Count()} files. First file: {group.First().FileName}");

            if (group.Key == "7z")
                yield return new SevenZipProcessor(group.ToList(), usenetClient, archivePassword, ct);

            else if (group.Key == "rar")
                foreach (var fileInfo in group)
                    yield return new RarProcessor(fileInfo, usenetClient, archivePassword, ct, connectionsPerRar);

            else if (group.Key == "multipart-mkv")
                yield return new MultipartMkvProcessor(group.ToList(), usenetClient, ct);

            else if (group.Key == "other")
                foreach (var fileInfo in group)
                    yield return new FileProcessor(fileInfo, usenetClient, ct);
        }

        yield break;

        string GetGroup(GetFileInfosStep.FileInfo x) => false ? "impossible"
            : FilenameUtil.Is7zFile(x.FileName) ? "7z"
            : x.IsRar || FilenameUtil.IsRarFile(x.FileName) ? "rar"
            : FilenameUtil.IsMultipartMkv(x.FileName) ? "multipart-mkv"
            : "other";
    }

    private async Task<DavItem?> GetMountFolder(DavDatabaseClient dbClient)
    {
        var query = from mountFolder in dbClient.Ctx.Items
            join categoryFolder in dbClient.Ctx.Items on mountFolder.ParentId equals categoryFolder.Id
            where mountFolder.Name == queueItem.JobName
                  && mountFolder.ParentId != null
                  && categoryFolder.Name == queueItem.Category
                  && categoryFolder.ParentId == DavItem.ContentFolder.Id
            select mountFolder;

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<DavItem> GetOrCreateCategoryFolder(DavDatabaseClient dbClient)
    {
        // if the category item already exists, return it
        var categoryFolder = await dbClient.GetDirectoryChildAsync(
            DavItem.ContentFolder.Id, queueItem.Category, ct).ConfigureAwait(false);
        if (categoryFolder is not null)
            return categoryFolder;

        // otherwise, create it
        categoryFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: DavItem.ContentFolder,
            name: queueItem.Category,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            releaseDate: null,
            lastHealthCheck: null
        );
        dbClient.Ctx.Items.Add(categoryFolder);
        return categoryFolder;
    }

    private Task<DavItem> CreateMountFolder
    (
        DavDatabaseClient dbClient,
        DavItem categoryFolder,
        DavItem? existingMountFolder,
        string duplicateNzbBehavior
    )
    {
        if (existingMountFolder is not null && duplicateNzbBehavior == "increment")
            return IncrementMountFolder(dbClient, categoryFolder);

        var mountFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: categoryFolder,
            name: queueItem.JobName,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            releaseDate: null,
            lastHealthCheck: null
        );
        dbClient.Ctx.Items.Add(mountFolder);
        return Task.FromResult(mountFolder);
    }

    private async Task<DavItem> IncrementMountFolder(DavDatabaseClient dbClient, DavItem categoryFolder)
    {
        for (var i = 2; i < 100; i++)
        {
            var name = $"{queueItem.JobName} ({i})";
            var existingMountFolder = await dbClient.GetDirectoryChildAsync(categoryFolder.Id, name, ct).ConfigureAwait(false);
            if (existingMountFolder is not null) continue;

            var mountFolder = DavItem.New(
                id: Guid.NewGuid(),
                parent: categoryFolder,
                name: name,
                fileSize: null,
                type: DavItem.ItemType.Directory,
                releaseDate: null,
                lastHealthCheck: null
            );
            dbClient.Ctx.Items.Add(mountFolder);
            return mountFolder;
        }

        throw new Exception("Duplicate nzb with more than 100 existing copies.");
    }

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, DateTime jobStartTime, string? errorMessage = null)
    {
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = DateTime.Now,
            FileName = queueItem.FileName,
            JobName = queueItem.JobName,
            Category = queueItem.Category,
            DownloadStatus = errorMessage == null
                ? HistoryItem.DownloadStatusOption.Completed
                : HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = queueItem.TotalSegmentBytes,
            DownloadTimeSeconds = (int)(DateTime.Now - jobStartTime).TotalSeconds,
            FailMessage = errorMessage,
            DownloadDirId = mountFolder?.Id,
        };
    }

    private async Task MarkQueueItemCompleted
    (
        DavDatabaseClient dbClient,
        DateTime startTime,
        string? error = null,
        Func<Task<DavItem?>>? databaseOperations = null
    )
    {
        dbClient.Ctx.ChangeTracker.Clear();
        var mountFolder = databaseOperations != null ? await databaseOperations.Invoke().ConfigureAwait(false) : null;
        var historyItem = CreateHistoryItem(mountFolder, startTime, error);
        var historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(historyItem, mountFolder, configManager);
        dbClient.Ctx.QueueItems.Remove(queueItem);
        dbClient.Ctx.HistoryItems.Add(historyItem);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemAdded, historySlot.ToJson());
        _ = RefreshMonitoredDownloads();
    }

    private async Task RefreshMonitoredDownloads()
    {
        var tasks = configManager
            .GetArrConfig()
            .GetArrClients()
            .Select(RefreshMonitoredDownloads);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RefreshMonitoredDownloads(ArrClient arrClient)
    {
        try
        {
            var downloadClients = await arrClient.GetDownloadClientsAsync().ConfigureAwait(false);
            if (downloadClients.All(x => x.Category != queueItem.Category)) return;
            var queueCount = await arrClient.GetQueueCountAsync().ConfigureAwait(false);
            if (queueCount < 300) await arrClient.RefreshMonitoredDownloads().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug($"Could not refresh monitored downloads for Arr instance: `{arrClient.Host}`. {e.Message}");
        }
    }
}