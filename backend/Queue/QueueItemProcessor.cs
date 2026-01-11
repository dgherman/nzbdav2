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
using NzbWebDAV.Exceptions;
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
        Log.Warning("!!! DEBUG: QueueItemProcessor STARTING for {JobName} ({Id}) !!!", queueItem.JobName, queueItem.Id);
        Log.Information("[QueueItemProcessor] Starting processing for {JobName} ({Id})", queueItem.JobName, queueItem.Id);
        // initialize
        var startTime = DateTime.Now;
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");

        // process the job
        try
        {
            Log.Debug("[QueueItemProcessor] Calling ProcessQueueItemAsync for {JobName}", queueItem.JobName);
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
            Log.Information("[QueueItemProcessor] Successfully completed processing for {JobName}", queueItem.JobName);
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (e.GetBaseException() is OperationCanceledException or TaskCanceledException)
        {
            try
            {
                Log.Warning("[QueueItemProcessor] Processing of queue item {JobName} ({Id}) was cancelled. Exception: {Exception}",
                    queueItem.JobName, queueItem.Id, e.GetBaseException().Message);
                using var scope = scopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                Log.Debug("[QueueItemProcessor] Marking cancelled item {JobName} as completed in history", queueItem.JobName);
                await MarkQueueItemCompleted(dbClient, startTime, error: "Processing was cancelled (timeout or manual cancellation)", failureReason: GetFailureReason(e)).ConfigureAwait(false);
                Log.Information("[QueueItemProcessor] Successfully moved cancelled item {JobName} to history", queueItem.JobName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QueueItemProcessor] Failed to mark cancelled queue item {JobName} as completed: {Error}",
                    queueItem.JobName, ex.Message);
            }
        }

        // when a retryable error is encountered
        // let's not remove the item from the queue
        // to give it a chance to retry. Simply
        // log the error and retry in a minute.
        catch (Exception e) when (e.IsRetryableDownloadException())
        {
            try
            {
                Log.Warning("[QueueItemProcessor] Retryable error processing job {JobName} ({Id}): {Message}. Will retry in 1 minute.",
                    queueItem.JobName, queueItem.Id, e.Message);
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
                    Log.Debug("[QueueItemProcessor] Set PauseUntil to {PauseUntil} for retryable error", item.PauseUntil);
                }
                else
                {
                    Log.Warning("[QueueItemProcessor] Could not find queue item {Id} to set PauseUntil", queueItem.Id);
                }
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QueueItemProcessor] Error handling retryable exception: {Error}", ex.Message);
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception e)
        {
            try
            {
                Log.Error(e, "[QueueItemProcessor] Fatal error processing job {JobName} ({Id}): {Message}. Moving to history as failed.",
                    queueItem.JobName, queueItem.Id, e.Message);
                using var scope = scopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                Log.Debug("[QueueItemProcessor] Marking failed item {JobName} as completed in history", queueItem.JobName);
                await MarkQueueItemCompleted(dbClient, startTime, error: e.Message, failureReason: GetFailureReason(e)).ConfigureAwait(false);
                Log.Information("[QueueItemProcessor] Successfully moved failed item {JobName} to history", queueItem.JobName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QueueItemProcessor] Failed to mark queue item {JobName} as completed: {Error}",
                    queueItem.JobName, ex.Message);
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
                await MarkQueueItemCompleted(dbClient, startTime, error, "Duplicate NZB", () => Task.FromResult(existingMountFolder)).ConfigureAwait(false);
                return;
            }
        }

        // GlobalOperationLimiter now handles all connection limits - no need for reserved connections
        var providerConfig = configManager.GetUsenetProviderConfig();
        var concurrency = configManager.GetMaxQueueConnections();
        Log.Information("[Queue] Processing '{JobName}': TotalConnections={TotalConnections}, MaxQueueConnections={MaxQueueConnections}", queueItem.JobName, providerConfig.TotalPooledConnections, concurrency);
        
        // Create a linked token for context propagation (more robust than setting on existing token)
        using var queueCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var _1 = queueCts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Queue, queueItem.JobName));
        var queueCt = queueCts.Token;

        // read the nzb document
        Log.Debug("[QueueItemProcessor] Parsing NZB document for {JobName}. NZB size: {NzbSizeBytes} bytes",
            queueItem.JobName, queueNzbContents.NzbContents.Length);
        var parseStartTime = DateTime.UtcNow;
        var documentBytes = Encoding.UTF8.GetBytes(queueNzbContents.NzbContents);
        using var stream = new MemoryStream(documentBytes);
        var nzb = await NzbDocument.LoadAsync(stream).ConfigureAwait(false);
        var archivePassword = nzb.MetaData.GetValueOrDefault("password")?.FirstOrDefault();
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();
        var parseElapsed = DateTime.UtcNow - parseStartTime;
        Log.Information("[QueueItemProcessor] Successfully parsed NZB for {JobName}. Files: {FileCount}, Total segments: {SegmentCount}, Elapsed: {ElapsedMs}ms",
            queueItem.JobName, nzbFiles.Count, nzbFiles.Sum(f => f.Segments.Count), parseElapsed.TotalMilliseconds);

        if (archivePassword != null)
        {
            Log.Information("[QueueItemProcessor] Archive password detected for {JobName}", queueItem.JobName);
        }

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/nzbdav-dev/nzbdav/issues/101
        Log.Debug("[QueueItemProcessor] Step 0: Pre-checking article existence against cache for {JobName}...", queueItem.JobName);
        var articlesToPrecheck = nzbFiles.SelectMany(x => x.Segments).Select(x => x.MessageId.Value);
        healthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);
        Log.Debug("[QueueItemProcessor] Step 0 complete: Pre-checked {ArticleCount} articles for {JobName}", articlesToPrecheck.Count(), queueItem.JobName);

        // step 1 -- get name and size of each nzb file
        Log.Information("[QueueItemProcessor] Step 1: Starting deobfuscation for {JobName}. Processing {FileCount} files (progress 0-50%)...",
            queueItem.JobName, nzbFiles.Count);
        var step1StartTime = DateTime.UtcNow;
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);

        Log.Debug("[QueueItemProcessor] Step 1a: Fetching first segments for {FileCount} files in {JobName}...", nzbFiles.Count, queueItem.JobName);
        var segments = await FetchFirstSegmentsStep.FetchFirstSegments(
            nzbFiles, usenetClient, configManager, queueCt, part1Progress).ConfigureAwait(false);
        Log.Information("[QueueItemProcessor] Step 1a complete: Fetched {SegmentCount} first segments for {JobName}",
            segments.Count, queueItem.JobName);

        Log.Debug("[QueueItemProcessor] Step 1b: Extracting Par2 file descriptors for {JobName}...", queueItem.JobName);
        var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
            segments, usenetClient, queueCt).ConfigureAwait(false);
        Log.Information("[QueueItemProcessor] Step 1b complete: Found {Par2Count} Par2 file descriptors for {JobName}",
            par2FileDescriptors.Count, queueItem.JobName);

        Log.Debug("[QueueItemProcessor] Step 1c: Building file info objects for {JobName}...", queueItem.JobName);
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);
        var step1Elapsed = DateTime.UtcNow - step1StartTime;
        Log.Information("[QueueItemProcessor] Step 1 complete: Deobfuscation finished for {JobName}. FileInfos: {FileInfoCount}, Elapsed: {ElapsedSeconds}s",
            queueItem.JobName, fileInfos.Count, step1Elapsed.TotalSeconds);

        // step 1b -- batch fetch file sizes for files without Par2 descriptors
        var filesWithoutSize = fileInfos.Where(f => f.FileSize == null).Select(f => f.NzbFile).ToList();
        if (filesWithoutSize.Count > 0)
        {
            Log.Debug("[QueueItemProcessor] Step 1d: Fetching file sizes for {FileCount} files without Par2 descriptors in {JobName}...",
                filesWithoutSize.Count, queueItem.JobName);
            var fileSizeStartTime = DateTime.UtcNow;
            var fileSizes = await usenetClient.GetFileSizesBatchAsync(filesWithoutSize, concurrency, queueCt).ConfigureAwait(false);
            var fileSizeElapsed = DateTime.UtcNow - fileSizeStartTime;
            Log.Information("[QueueItemProcessor] Step 1d complete: Fetched {FileCount} file sizes for {JobName}. Elapsed: {ElapsedSeconds}s",
                fileSizes.Count, queueItem.JobName, fileSizeElapsed.TotalSeconds);
            foreach (var fileInfo in fileInfos.Where(f => f.FileSize == null))
            {
                if (fileSizes.TryGetValue(fileInfo.NzbFile, out var size))
                {
                    fileInfo.FileSize = size;
                }
            }
        }

        // step 2 -- perform file processing
        Log.Information("[QueueItemProcessor] Step 2: Creating file processors for {JobName}. FileInfos: {FileInfoCount}",
            queueItem.JobName, fileInfos.Count);
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword, queueCt).ToList();
        Log.Information("[QueueItemProcessor] Step 2: Created {ProcessorCount} file processors for {JobName} (progress 50-100%)",
            fileProcessors.Count, queueItem.JobName);

        // Safe limit: totalConnections / maxConnectionsPerFile = 145 / 5 = 29
        var fileConcurrency = Math.Max(1, Math.Min(concurrency, providerConfig.TotalPooledConnections / 5));
        Log.Debug("[QueueItemProcessor] Step 2: File processing concurrency: {FileConcurrency} for {JobName}", fileConcurrency, queueItem.JobName);

        var part2Progress = progress
            .Offset(50)
            .Scale(50, 100)
            .ToPercentage(fileProcessors.Count);

        Log.Information("[QueueItemProcessor] Step 2: Starting file processing for {ProcessorCount} processors in {JobName}...",
            fileProcessors.Count, queueItem.JobName);
        var step2StartTime = DateTime.UtcNow;
        var fileProcessingResultsAll = await fileProcessors
            .Select(x => x!.ProcessAsync())
            .WithConcurrencyAsync(fileConcurrency)
            .GetAllAsync(queueCt, part2Progress).ConfigureAwait(false);
        var fileProcessingResults = fileProcessingResultsAll
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
        var step2Elapsed = DateTime.UtcNow - step2StartTime;
        Log.Information("[QueueItemProcessor] Step 2 complete: File processing finished for {JobName}. Results: {ResultCount}, Elapsed: {ElapsedSeconds}s",
            queueItem.JobName, fileProcessingResults.Count, step2Elapsed.TotalSeconds);

        // step 3 -- Optionally check full article existence
        var checkedFullHealth = false;
        if (configManager.IsEnsureArticleExistenceEnabled())
        {
            var articlesToCheck = fileInfos
                .Where(x => x.IsRar || FilenameUtil.IsImportantFileType(x.FileName))
                .SelectMany(x => x.NzbFile.GetSegmentIds())
                .ToList();
            Log.Information("[QueueItemProcessor] Step 3: Starting article existence check for {JobName}. Articles: {ArticleCount} (progress 100+)",
                queueItem.JobName, articlesToCheck.Count);
            var step3StartTime = DateTime.UtcNow;
            var part3Progress = progress
                .Offset(100)
                .ToPercentage(articlesToCheck.Count);
            await usenetClient.CheckAllSegmentsAsync(articlesToCheck, concurrency, part3Progress, queueCt).ConfigureAwait(false);
            var step3Elapsed = DateTime.UtcNow - step3StartTime;
            Log.Information("[QueueItemProcessor] Step 3 complete: Article existence check finished for {JobName}. Elapsed: {ElapsedSeconds}s",
                queueItem.JobName, step3Elapsed.TotalSeconds);
            checkedFullHealth = true;
        }
        else
        {
            Log.Debug("[QueueItemProcessor] Step 3: Skipping article existence check (disabled in config) for {JobName}", queueItem.JobName);
        }

        // Scope 2: Final DB update
        Log.Information("[QueueItemProcessor] Step 4: Starting database update for {JobName}...", queueItem.JobName);
        var step4StartTime = DateTime.UtcNow;
        using (var scope = scopeFactory.CreateScope())
        {
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            // update the database
            await MarkQueueItemCompleted(dbClient, startTime, error: null, failureReason: null, databaseOperations: async () =>
            {
                Log.Debug("[QueueItemProcessor] Step 4a: Creating category and mount folders for {JobName}...", queueItem.JobName);
                var categoryFolder = await GetOrCreateCategoryFolder(dbClient).ConfigureAwait(false);
                var mountFolder = await CreateMountFolder(dbClient, categoryFolder, existingMountFolder, duplicateNzbBehavior).ConfigureAwait(false);

                Log.Debug("[QueueItemProcessor] Step 4b: Running aggregators for {JobName}...", queueItem.JobName);
                new RarAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
                new FileAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
                new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
                new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);

                Log.Debug("[QueueItemProcessor] Step 4c: Running post-processors for {JobName}...", queueItem.JobName);
                // post-processing
                new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
                new BlacklistedExtensionPostProcessor(configManager, dbClient).RemoveBlacklistedExtensions();

                // validate video files found
                if (configManager.IsEnsureImportableVideoEnabled())
                {
                    Log.Debug("[QueueItemProcessor] Step 4d: Validating importable video for {JobName}...", queueItem.JobName);
                    new EnsureImportableVideoValidator(dbClient).ThrowIfValidationFails();
                }

                // create strm files, if necessary
                if (configManager.GetImportStrategy() == "strm")
                {
                    Log.Debug("[QueueItemProcessor] Step 4e: Creating STRM files for {JobName}...", queueItem.JobName);
                    await new CreateStrmFilesPostProcessor(configManager, dbClient).CreateStrmFilesAsync().ConfigureAwait(false);
                }

                Log.Debug("[QueueItemProcessor] Step 4f: All database operations complete for {JobName}", queueItem.JobName);
                return mountFolder;
            }).ConfigureAwait(false);
        }
        var step4Elapsed = DateTime.UtcNow - step4StartTime;
        Log.Information("[QueueItemProcessor] Step 4 complete: Database update finished for {JobName}. Elapsed: {ElapsedSeconds}s",
            queueItem.JobName, step4Elapsed.TotalSeconds);
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword,
        CancellationToken ct
    )
    {
        Log.Debug("[GetFileProcessors] Processing {FileInfoCount} file infos", fileInfos.Count);
        var maxConnections = configManager.GetMaxQueueConnections();
        
        // Smart Grouping: Group by base name first to keep multi-part files together
        var baseGroups = fileInfos
            .DistinctBy(x => x.FileName)
            .GroupBy(x => FilenameUtil.GetMultipartBaseName(x.FileName))
            .ToList();

        Log.Information("[GetFileProcessors] Identified {GroupCount} base file groups", baseGroups.Count);

        // Determine group type for each base group
        var finalGroups = new List<(string Type, List<GetFileInfosStep.FileInfo> Files)>();
        foreach (var baseGroup in baseGroups)
        {
            var files = baseGroup.ToList();
            var groupType = "other";

            // If ANY file in the group has RAR magic or extension, the whole group is RAR
            if (files.Any(x => x.IsRar || FilenameUtil.IsRarFile(x.FileName)))
            {
                groupType = "rar";
            }
            else if (files.Any(x => x.IsSevenZip || FilenameUtil.Is7zFile(x.FileName)))
            {
                groupType = "7z";
            }
            else if (files.Any(x => FilenameUtil.IsMultipartMkv(x.FileName)))
            {
                groupType = "multipart-mkv";
            }

            finalGroups.Add((groupType, files));
        }

        Log.Information("[GetFileProcessors] Classified groups: {GroupSummary}",
            string.Join(", ", finalGroups.GroupBy(g => g.Type).Select(g => $"{g.Key}={g.Count()}")));

        // Calculate adaptive concurrency per RAR to avoid connection pool exhaustion
        var rarGroupCount = finalGroups.Count(g => g.Type == "rar");
        var connectionsPerRar = rarGroupCount > 0
            ? Math.Max(1, Math.Min(5, maxConnections / Math.Max(1, rarGroupCount / 3)))
            : 1;

        foreach (var group in finalGroups)
        {
            Log.Debug("[GetFileProcessors] Processing group type '{GroupType}' with {FileCount} files. Base name: {BaseName}",
                group.Type, group.Files.Count, FilenameUtil.GetMultipartBaseName(group.Files.First().FileName));

            if (group.Type == "7z")
            {
                Log.Debug("[GetFileProcessors] Creating SevenZipProcessor for {FileCount} files", group.Files.Count);
                yield return new SevenZipProcessor(group.Files, usenetClient, archivePassword, ct);
            }

            else if (group.Type == "rar")
            {
                var rarFiles = group.Files;
                Log.Debug("[GetFileProcessors] Creating RarProcessor for group: {BaseName} ({Count} parts)", 
                    FilenameUtil.GetMultipartBaseName(rarFiles.First().FileName), rarFiles.Count);
                yield return new RarProcessor(rarFiles, usenetClient, archivePassword, ct, connectionsPerRar);
            }

            else if (group.Type == "multipart-mkv")
            {
                Log.Debug("[GetFileProcessors] Creating MultipartMkvProcessor for {FileCount} files", group.Files.Count);
                yield return new MultipartMkvProcessor(group.Files, usenetClient, ct);
            }

            else
            {
                Log.Debug("[GetFileProcessors] Creating {ProcessorCount} FileProcessors", group.Files.Count);
                foreach (var fileInfo in group.Files)
                {
                    yield return new FileProcessor(fileInfo, usenetClient, ct);
                }
            }
        }
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
            id: GuidUtil.CreateDeterministic(DavItem.ContentFolder.Id, queueItem.Category),
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
            id: GuidUtil.CreateDeterministic(categoryFolder.Id, queueItem.JobName),
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
                id: GuidUtil.CreateDeterministic(categoryFolder.Id, name),
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

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, DateTime jobStartTime, string? errorMessage = null, string? failureReason = null)
    {
        var now = DateTime.Now;
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = now,
            CompletedAt = now,
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
            NzbContents = queueNzbContents.NzbContents,
            FailureReason = failureReason,
        };
    }

    private static string GetFailureReason(Exception exception)
    {
        var baseException = exception.GetBaseException();
        return baseException switch
        {
            UsenetArticleNotFoundException => "Missing Articles",
            OperationCanceledException or TaskCanceledException => "Timeout/Cancelled",
            CouldNotConnectToUsenetException or CouldNotLoginToUsenetException => "Connection Error",
            PasswordProtectedRarException or PasswordProtected7zException => "Password Protected",
            UnsupportedRarCompressionMethodException or Unsupported7zCompressionMethodException => "Unsupported Format",
            NoVideoFilesFoundException => "No Video Files",
            _ => "Unknown Error"
        };
    }

    private async Task MarkQueueItemCompleted
    (
        DavDatabaseClient dbClient,
        DateTime startTime,
        string? error = null,
        string? failureReason = null,
        Func<Task<DavItem?>>? databaseOperations = null
    )
    {
        Log.Information("[QueueItemProcessor] MarkQueueItemCompleted called for {JobName} ({Id}). Error: {Error}",
            queueItem.JobName, queueItem.Id, error ?? "None");

        dbClient.Ctx.ChangeTracker.Clear();
        var mountFolder = databaseOperations != null ? await databaseOperations.Invoke().ConfigureAwait(false) : null;
        var historyItem = CreateHistoryItem(mountFolder, startTime, error, failureReason);
        var historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(historyItem, mountFolder, configManager);

        Log.Debug("[QueueItemProcessor] Removing queue item {Id} from QueueItems table", queueItem.Id);
        // Ensure queueItem is attached to this context before removing
        dbClient.Ctx.QueueItems.Entry(queueItem).State = EntityState.Deleted;

        Log.Debug("[QueueItemProcessor] Adding history item {Id} to HistoryItems table. Status: {Status}",
            historyItem.Id, historyItem.DownloadStatus);
        dbClient.Ctx.HistoryItems.Add(historyItem);

        Log.Debug("[QueueItemProcessor] Saving changes to database...");
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Information("[QueueItemProcessor] Successfully moved queue item {JobName} ({Id}) to history. Status: {Status}, CompletedAt: {CompletedAt}",
            queueItem.JobName, historyItem.Id, historyItem.DownloadStatus, historyItem.CompletedAt);

        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemAdded, historySlot.ToJson());
        _ = RefreshMonitoredDownloads();

        // All history items (including failed) are now retained for 1 hour via ArrMonitoringService cleanup
    }

    private async Task RemoveFailedHistoryItemAfterDelay(Guid id, TimeSpan delay)
    {
        try
        {
            Log.Information("[QueueItemProcessor] Scheduling auto-removal of failed item {Id} in {Minutes} minutes", id, delay.TotalMinutes);
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);

            using var scope = scopeFactory.CreateScope();
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            Log.Information("[QueueItemProcessor] Auto-removing failed item {Id}", id);
            
            // Remove the item
            await dbClient.RemoveHistoryItemsAsync([id], true, CancellationToken.None).ConfigureAwait(false);
            await dbClient.SaveChanges(CancellationToken.None).ConfigureAwait(false);
            
            // Notify frontend
            _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, id.ToString());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[QueueItemProcessor] Failed to auto-remove history item {Id}", id);
        }
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
            Log.Debug("Could not refresh monitored downloads for Arr instance: {ArrHost}. {Message}", arrClient.Host, e.Message);
        }
    }
}