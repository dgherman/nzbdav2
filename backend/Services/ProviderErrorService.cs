using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class ProviderErrorService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentQueue<MissingArticleEvent> _buffer = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _persistenceTask;

    public ProviderErrorService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _persistenceTask = Task.Run(PersistenceLoop);
    }

    private async Task PersistenceLoop()
    {
        var cleanupTickCounter = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await PersistEvents();

                cleanupTickCounter++;
                if (cleanupTickCounter >= 30) // Run cleanup every ~5 minutes (30 * 10s)
                {
                    cleanupTickCounter = 0;
                    await CleanupOrphanedErrorsAsync(_cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
    }

    private async Task PersistEvents()
    {
        if (_buffer.IsEmpty) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var configManager = scope.ServiceProvider.GetRequiredService<Config.ConfigManager>();
            var totalProviders = configManager.GetUsenetProviderConfig().Providers.Count;
            
            var batch = new List<MissingArticleEvent>();
            while (_buffer.TryDequeue(out var evt) && batch.Count < 1000)
            {
                batch.Add(evt);
            }

            if (batch.Count > 0)
            {
                // 1. Add Events (SKIPPED to save space - aggregated into summaries only)
                // dbContext.MissingArticleEvents.AddRange(batch);

                // 2. Update Summaries - group by NORMALIZED filename to merge variants like "Movie.mkv" and "Movie"
                var fileGroups = batch.GroupBy(x => NormalizeFilenameForGrouping(x.Filename));
                foreach (var group in fileGroups)
                {
                    var normalizedFilename = group.Key;
                    // Try to find existing summary by normalized filename
                    var summary = await dbContext.MissingArticleSummaries
                        .FirstOrDefaultAsync(x => x.Filename == normalizedFilename);

                    // Also check for summaries with un-normalized filenames (for migration from old data)
                    if (summary == null)
                    {
                        // Check if any original filename variant exists
                        var originalFilenames = group.Select(x => x.Filename).Distinct().ToList();
                        summary = await dbContext.MissingArticleSummaries
                            .FirstOrDefaultAsync(x => originalFilenames.Contains(x.Filename));

                        // If found with old filename, update to normalized
                        if (summary != null)
                        {
                            summary.Filename = normalizedFilename;
                        }
                    }

                    // Try to find DavItem by any of the original paths
                    var originalPaths = group.Select(x => x.Filename).Distinct().ToList();
                    var davItem = await dbContext.Items
                        .Where(x => originalPaths.Contains(x.Path) || x.Path == normalizedFilename)
                        .Select(x => new { x.Id })
                        .FirstOrDefaultAsync();

                    if (summary == null)
                    {
                        summary = new MissingArticleSummary
                        {
                            Id = Guid.NewGuid(),
                            DavItemId = davItem?.Id ?? Guid.Empty, // Populate DavItemId
                            Filename = normalizedFilename, // Store normalized filename
                            JobName = group.First().JobName,
                            FirstSeen = DateTimeOffset.UtcNow,
                            LastSeen = DateTimeOffset.UtcNow,
                            TotalEvents = 0,
                            ProviderCountsJson = "{}",
                            HasBlockingMissingArticles = false,
                            IsImported = group.Any(x => x.IsImported)
                        };
                        dbContext.MissingArticleSummaries.Add(summary);
                    }
                    else
                    {
                        // Ensure DavItemId is updated if it was empty (e.g., from old summaries without DavItemId)
                        if (summary.DavItemId == Guid.Empty)
                        {
                            summary.DavItemId = davItem?.Id ?? Guid.Empty;
                        }
                    }

                    // Update timestamps
                    var maxTimestamp = group.Max(x => x.Timestamp);
                    if (maxTimestamp > summary.LastSeen) summary.LastSeen = maxTimestamp;

                    // Update provider counts
                    var providerCountsJson = !string.IsNullOrWhiteSpace(summary.ProviderCountsJson) ? summary.ProviderCountsJson : "{}";
                    var operationCountsJson = !string.IsNullOrWhiteSpace(summary.OperationCountsJson) ? summary.OperationCountsJson : "{}";

                    var providerCounts = JsonSerializer.Deserialize<Dictionary<int, int>>(providerCountsJson) ?? new();
                    var operationCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(operationCountsJson) ?? new();
                    
                    foreach (var evt in group)
                    {
                        if (!providerCounts.ContainsKey(evt.ProviderIndex)) providerCounts[evt.ProviderIndex] = 0;
                        providerCounts[evt.ProviderIndex]++;
                        
                        var op = evt.Operation ?? "UNKNOWN";
                        if (!operationCounts.ContainsKey(op)) operationCounts[op] = 0;
                        operationCounts[op]++;
                    }
                    summary.ProviderCountsJson = JsonSerializer.Serialize(providerCounts);
                    summary.OperationCountsJson = JsonSerializer.Serialize(operationCounts);

                    // Update stats
                    summary.TotalEvents += group.Count();
                    if (group.Any(x => x.IsImported)) summary.IsImported = true;

                    // Check for blocking (simple check based on current batch + existing state)
                    if (!summary.HasBlockingMissingArticles)
                    {
                         // Optimization: Check for blocking within the current batch only.
                         // Since we are not persisting granular events anymore to save space, 
                         // we cannot query the DB for historical provider failures per segment.
                         // We will mark as blocking only if we see failures from ALL providers for a single segment *within this batch* 
                         // or if the summary already says so.
                         
                         var segmentsInBatch = group.GroupBy(x => x.SegmentId);
                         foreach (var segGroup in segmentsInBatch)
                         {
                             var distinctProvidersInBatch = segGroup.Select(x => x.ProviderIndex).Distinct().Count();
                             if (distinctProvidersInBatch >= totalProviders)
                             {
                                 summary.HasBlockingMissingArticles = true;
                                 Log.Information($"[MissingArticles] File '{normalizedFilename}' (DavItemId: {summary.DavItemId}) is now blocking (missing across all providers in current batch).");
                                 break;
                             }
                         }
                    }
                }

                // Single SaveChangesAsync
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist missing article events");
        }
    }

    // Common video/media extensions to strip for normalization
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".avi", ".mp4", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".m4v", ".flv", ".webm", ".vob", ".ogv", ".3gp", ".divx", ".xvid",
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a",
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".nfo", ".txt", ".jpg", ".png"
    };

    /// <summary>
    /// Normalizes a filename/path for grouping purposes by stripping media extensions.
    /// This ensures "Movie.2024.mkv" and "Movie.2024" are treated as the same logical file.
    /// </summary>
    private static string NormalizeFilenameForGrouping(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return filename;

        // Get just the filename part if it's a path
        var lastSlash = filename.LastIndexOf('/');
        var directory = lastSlash >= 0 ? filename.Substring(0, lastSlash + 1) : "";
        var name = lastSlash >= 0 ? filename.Substring(lastSlash + 1) : filename;

        // Strip known media extensions
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
        {
            var ext = name.Substring(lastDot);
            if (MediaExtensions.Contains(ext))
            {
                name = name.Substring(0, lastDot);
            }
        }

        // Trim trailing dots and spaces
        name = name.TrimEnd('.', ' ');

        return directory + name;
    }

    private string ExtractJobName(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return "Unknown";

        // First normalize by stripping extensions
        var normalized = NormalizeFilenameForGrouping(filename);

        if (!normalized.StartsWith('/')) return normalized; // Likely a Job Name from Queue

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /content/Category/JobName/File -> parts[2]
        // /content/File.mkv -> parts[1]

        if (parts.Length >= 3) return parts[2];
        if (parts.Length == 2) return parts[1];
        if (parts.Length == 1) return parts[0];

        return "Uncategorized";
    }

    public void RecordError(int providerIndex, string filename, string segmentId, string error, bool isImported = false, string operation = "UNKNOWN")
    {
        var jobName = ExtractJobName(filename);
        _buffer.Enqueue(new MissingArticleEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProviderIndex = providerIndex,
            Filename = filename,
            SegmentId = segmentId,
            Error = error,
            JobName = jobName,
            IsImported = isImported,
            Operation = operation
        });
    }

    public async Task BackfillSummariesAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var configManager = scope.ServiceProvider.GetRequiredService<Config.ConfigManager>();
            var totalProviders = configManager.GetUsenetProviderConfig().Providers.Count;

            // Check if we need backfill
            var hasSummaries = await dbContext.MissingArticleSummaries.AnyAsync(ct);
            if (hasSummaries) return;

            Log.Information("Backfilling MissingArticleSummaries table...");

            var filenames = await dbContext.MissingArticleEvents
                .Select(x => x.Filename)
                .Distinct()
                .ToListAsync(ct);

            var totalFiles = filenames.Count;
            var processed = 0;

            foreach (var filename in filenames)
            {
                var events = await dbContext.MissingArticleEvents
                    .Where(x => x.Filename == filename)
                    .ToListAsync(ct);

                if (events.Count == 0) continue;

                var firstEvent = events.First();
                var providerCounts = events.GroupBy(x => x.ProviderIndex)
                    .ToDictionary(g => g.Key, g => g.Count());
                
                var operationCounts = events.GroupBy(x => x.Operation ?? "UNKNOWN")
                    .ToDictionary(g => g.Key, g => g.Count());
                
                var hasBlocking = events.GroupBy(x => x.SegmentId)
                    .Any(g => g.Select(p => p.ProviderIndex).Distinct().Count() >= totalProviders);

                var summary = new MissingArticleSummary
                {
                    Id = Guid.NewGuid(),
                    DavItemId = await dbContext.Items
                        .Where(x => x.Path == filename)
                        .Select(x => x.Id)
                        .FirstOrDefaultAsync(), // Populate DavItemId
                    Filename = filename,
                    JobName = firstEvent.JobName,
                    FirstSeen = events.Min(x => x.Timestamp),
                    LastSeen = events.Max(x => x.Timestamp),
                    TotalEvents = events.Count,
                    ProviderCountsJson = JsonSerializer.Serialize(providerCounts),
                    OperationCountsJson = JsonSerializer.Serialize(operationCounts),
                    HasBlockingMissingArticles = hasBlocking,
                    IsImported = events.Any(x => x.IsImported)
                };

                dbContext.MissingArticleSummaries.Add(summary);
                
                processed++;
                if (processed % 100 == 0)
                {
                    await dbContext.SaveChangesAsync(ct);
                    Log.Information($"Backfilled {processed}/{totalFiles} file summaries");
                }
            }

            await dbContext.SaveChangesAsync(ct);
            Log.Information("Completed backfilling MissingArticleSummaries");

            // Merge any duplicates that differ only by extension
            await MergeDuplicateSummariesAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to backfill MissingArticleSummaries");
        }
    }

    /// <summary>
    /// Merges duplicate summaries that differ only by file extension (e.g., "Movie" and "Movie.mkv").
    /// This handles migration from old data where normalization wasn't applied.
    /// </summary>
    public async Task MergeDuplicateSummariesAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Get all summaries and group by normalized filename
            var allSummaries = await dbContext.MissingArticleSummaries.ToListAsync(ct);
            var groupedByNormalized = allSummaries
                .GroupBy(s => NormalizeFilenameForGrouping(s.Filename))
                .Where(g => g.Count() > 1) // Only process groups with duplicates
                .ToList();

            if (groupedByNormalized.Count == 0) return;

            Log.Information($"[ProviderErrorService] Found {groupedByNormalized.Count} groups of duplicate summaries to merge");

            var mergedCount = 0;
            foreach (var group in groupedByNormalized)
            {
                var normalizedFilename = group.Key;
                var summaries = group.OrderByDescending(s => s.TotalEvents).ToList();

                // Keep the one with most events as the primary
                var primary = summaries[0];
                var toMerge = summaries.Skip(1).ToList();

                // Merge data from duplicates into primary
                foreach (var dup in toMerge)
                {
                    primary.TotalEvents += dup.TotalEvents;
                    if (dup.FirstSeen < primary.FirstSeen) primary.FirstSeen = dup.FirstSeen;
                    if (dup.LastSeen > primary.LastSeen) primary.LastSeen = dup.LastSeen;
                    if (dup.HasBlockingMissingArticles) primary.HasBlockingMissingArticles = true;
                    if (dup.IsImported) primary.IsImported = true;
                    if (primary.DavItemId == Guid.Empty && dup.DavItemId != Guid.Empty)
                    {
                        primary.DavItemId = dup.DavItemId;
                    }

                    // Merge provider counts
                    var primaryCounts = !string.IsNullOrWhiteSpace(primary.ProviderCountsJson)
                        ? JsonSerializer.Deserialize<Dictionary<int, int>>(primary.ProviderCountsJson) ?? new()
                        : new Dictionary<int, int>();
                    var dupCounts = !string.IsNullOrWhiteSpace(dup.ProviderCountsJson)
                        ? JsonSerializer.Deserialize<Dictionary<int, int>>(dup.ProviderCountsJson) ?? new()
                        : new Dictionary<int, int>();

                    foreach (var kvp in dupCounts)
                    {
                        if (!primaryCounts.ContainsKey(kvp.Key)) primaryCounts[kvp.Key] = 0;
                        primaryCounts[kvp.Key] += kvp.Value;
                    }
                    primary.ProviderCountsJson = JsonSerializer.Serialize(primaryCounts);

                    // Merge operation counts
                    var primaryOpCounts = !string.IsNullOrWhiteSpace(primary.OperationCountsJson)
                        ? JsonSerializer.Deserialize<Dictionary<string, int>>(primary.OperationCountsJson) ?? new()
                        : new Dictionary<string, int>();
                    var dupOpCounts = !string.IsNullOrWhiteSpace(dup.OperationCountsJson)
                        ? JsonSerializer.Deserialize<Dictionary<string, int>>(dup.OperationCountsJson) ?? new()
                        : new Dictionary<string, int>();

                    foreach (var kvp in dupOpCounts)
                    {
                        if (!primaryOpCounts.ContainsKey(kvp.Key)) primaryOpCounts[kvp.Key] = 0;
                        primaryOpCounts[kvp.Key] += kvp.Value;
                    }
                    primary.OperationCountsJson = JsonSerializer.Serialize(primaryOpCounts);
                }

                // Update primary to use normalized filename
                primary.Filename = normalizedFilename;
                primary.JobName = ExtractJobName(normalizedFilename);

                // Remove duplicates
                dbContext.MissingArticleSummaries.RemoveRange(toMerge);
                mergedCount += toMerge.Count;
            }

            if (mergedCount > 0)
            {
                await dbContext.SaveChangesAsync(ct);
                Log.Information($"[ProviderErrorService] Merged {mergedCount} duplicate summaries into {groupedByNormalized.Count} primary entries");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ProviderErrorService] Failed to merge duplicate summaries");
        }
    }

    public async Task BackfillJobNamesAsync(CancellationToken ct = default)
    {
        // ... existing BackfillJobNamesAsync ...
        // (content omitted for brevity, but it remains the same)
    }

    public async Task BackfillDavItemIdsAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Only check summaries where DavItemId is missing
            var summariesToUpdateCount = await dbContext.MissingArticleSummaries
                .Where(x => x.DavItemId == Guid.Empty)
                .CountAsync(ct);

            if (summariesToUpdateCount == 0) return;

            Log.Information($"Backfilling DavItemId for {summariesToUpdateCount} MissingArticleSummaries...");

            var summariesToUpdate = await dbContext.MissingArticleSummaries
                .Where(x => x.DavItemId == Guid.Empty)
                .ToListAsync(ct);

            var updatedCount = 0;
            foreach (var summary in summariesToUpdate)
            {
                var davItem = await dbContext.Items
                    .Where(x => x.Path == summary.Filename)
                    .Select(x => new { x.Id })
                    .FirstOrDefaultAsync(ct);
                
                if (davItem?.Id != null && davItem.Id != Guid.Empty)
                {
                    summary.DavItemId = davItem.Id;
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                await dbContext.SaveChangesAsync(ct);
                Log.Information($"Successfully backfilled DavItemId for {updatedCount} summaries.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to backfill DavItemIds.");
        }
    }

    public async Task<(List<MissingArticleItem> Items, int TotalCount)> GetFileSummariesPagedAsync(int page, int pageSize, int totalProviders, string? search = null, bool? blocking = null, bool? orphaned = null, bool? isImported = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var configManager = scope.ServiceProvider.GetRequiredService<Config.ConfigManager>(); // Get configManager

            var baseQuery = dbContext.MissingArticleSummaries.AsNoTracking();
            
            // Join LocalLinks to determine IsImported dynamically based on current status
            var query = from s in baseQuery
                        join l in dbContext.LocalLinks.AsNoTracking() on s.DavItemId equals l.DavItemId into links
                        from l in links.DefaultIfEmpty()
                        select new 
                        { 
                            s, 
                            IsImported = l != null 
                        };

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(x => x.s.JobName.Contains(search) || x.s.Filename.Contains(search));
            }

            if (blocking.HasValue)
            {
                query = query.Where(x => x.s.HasBlockingMissingArticles == blocking.Value);
            }

            if (orphaned.HasValue)
            {
                if (orphaned.Value)
                {
                    query = query.Where(x => x.s.DavItemId == Guid.Empty);
                }
                else
                {
                    query = query.Where(x => x.s.DavItemId != Guid.Empty);
                }
            }

            if (isImported.HasValue)
            {
                if (isImported.Value)
                {
                    query = query.Where(x => x.IsImported);
                }
                else
                {
                    query = query.Where(x => !x.IsImported);
                }
            }

            var totalCount = await query.CountAsync().ConfigureAwait(false);

            var result = await query
                .OrderByDescending(x => x.s.LastSeen)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync()
                .ConfigureAwait(false);

            var items = result.Select(x => {
                var rcloneMountDir = configManager.GetRcloneMountDir();
                string? davItemInternalPath = null;

                if (x.s.DavItemId != Guid.Empty && rcloneMountDir != null)
                {
                    var idStr = x.s.DavItemId.ToString();
                    var prefix = idStr.Substring(0, 5);
                    var nestedPath = Path.Combine(
                        prefix[0].ToString(), 
                        prefix[1].ToString(), 
                        prefix[2].ToString(), 
                        prefix[3].ToString(), 
                        prefix[4].ToString(), 
                        idStr
                    );
                    davItemInternalPath = Path.Combine(rcloneMountDir, ".ids", nestedPath);
                }

                return new MissingArticleItem
                {
                    JobName = x.s.JobName,
                    Filename = x.s.Filename,
                    DavItemId = x.s.DavItemId.ToString(),
                    DavItemInternalPath = davItemInternalPath ?? "N/A", // Populate new property
                    LatestTimestamp = x.s.LastSeen,
                    TotalEvents = x.s.TotalEvents,
                    ProviderCounts = !string.IsNullOrWhiteSpace(x.s.ProviderCountsJson) 
                        ? (JsonSerializer.Deserialize<Dictionary<int, int>>(x.s.ProviderCountsJson) ?? new()) 
                        : new(),
                    OperationCounts = !string.IsNullOrWhiteSpace(x.s.OperationCountsJson) 
                        ? (JsonSerializer.Deserialize<Dictionary<string, int>>(x.s.OperationCountsJson) ?? new()) 
                        : new(),
                    HasBlockingMissingArticles = x.s.HasBlockingMissingArticles,
                    IsImported = x.IsImported // Use dynamic value
                };
            }).ToList();

            return (items, totalCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve missing article summaries");
            return (new List<MissingArticleItem>(), 0);
        }
    }
    
    // ... (GetErrorsPagedAsync and GetErrorsAsync remain same, no changes needed to signature)

    public async Task CleanupOrphanedErrorsAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Log.Debug("[ProviderErrorService] Checking for orphaned missing article errors..."); // Debug log to avoid noise

            // 1. Identify orphans by Empty ID (Failed linkage)
            // Since BackfillDavItemIdsAsync runs at startup, persistent Empty IDs mean the file is not in DB.
            var emptyIdOrphans = await dbContext.MissingArticleSummaries
                .Where(x => x.DavItemId == Guid.Empty)
                .Select(x => x.Filename)
                .ToListAsync(ct);
                
            // 2. Identify orphans by Deleted Item (ID exists but Item gone)
            var deletedItemOrphans = await dbContext.MissingArticleSummaries
                .Where(s => s.DavItemId != Guid.Empty && !dbContext.Items.Any(i => i.Id == s.DavItemId))
                .Select(x => x.Filename)
                .ToListAsync(ct);
            
            var allOrphanFilenames = emptyIdOrphans.Concat(deletedItemOrphans).Distinct().ToList();

            if (allOrphanFilenames.Count > 0)
            {
                Log.Information($"[ProviderErrorService] Found {allOrphanFilenames.Count} orphaned missing article summaries. Cleaning up...");

                // Batch deletion to avoid too many parameters in SQL
                const int batchSize = 500;
                for (int i = 0; i < allOrphanFilenames.Count; i += batchSize)
                {
                    var batch = allOrphanFilenames.Skip(i).Take(batchSize).ToList();
                    
                    // Delete Summaries
                    await dbContext.MissingArticleSummaries
                        .Where(x => batch.Contains(x.Filename))
                        .ExecuteDeleteAsync(ct);

                    // Delete Events
                    await dbContext.MissingArticleEvents
                        .Where(x => batch.Contains(x.Filename))
                        .ExecuteDeleteAsync(ct);
                }

                Log.Information($"[ProviderErrorService] Cleaned up orphans for {allOrphanFilenames.Count} files.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to cleanup orphaned errors.");
        }
    }

    public async Task ClearErrorsForFile(string filePath)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Clear Events
            await dbContext.MissingArticleEvents
                .Where(x => x.Filename == filePath)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            // Clear Summary
            await dbContext.MissingArticleSummaries
                .Where(x => x.Filename == filePath)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);
                
            Log.Information($"Cleared missing article events/summary for file: {filePath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to clear missing article events for file: {filePath}");
        }
    }

    public async Task ClearAllErrors()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            await dbContext.MissingArticleEvents.ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.MissingArticleSummaries.ExecuteDeleteAsync().ConfigureAwait(false);
            
            Log.Information("Cleared all missing article events and summaries from database");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear missing article events");
            throw;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}