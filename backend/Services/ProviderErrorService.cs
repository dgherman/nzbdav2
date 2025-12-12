using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
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
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await PersistEvents();
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
            
            var batch = new List<MissingArticleEvent>();
            while (_buffer.TryDequeue(out var evt) && batch.Count < 1000)
            {
                batch.Add(evt);
            }

            if (batch.Count > 0)
            {
                dbContext.MissingArticleEvents.AddRange(batch);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist missing article events");
        }
    }

    private string ExtractJobName(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return "Unknown";
        if (!filename.StartsWith('/')) return filename; // Likely a Job Name from Queue
        
        var parts = filename.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /content/Category/JobName/File -> parts[2]
        // /content/File.mkv -> parts[1]
        
        if (parts.Length >= 3) return parts[2];
        if (parts.Length == 2) return parts[1];
        if (parts.Length == 1) return parts[0];
        
        return "Uncategorized";
    }

    public void RecordError(int providerIndex, string filename, string segmentId, string error, bool isImported = false)
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
            IsImported = isImported
        });
    }

    public async Task BackfillJobNamesAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Check if there are any records needing update
            var count = await dbContext.MissingArticleEvents
                .Where(x => x.JobName == "")
                .CountAsync(ct);

            if (count == 0) return;

            Log.Information($"Backfilling JobNames for {count} missing article events...");

            var pageSize = 1000;
            var processed = 0;

            while (processed < count)
            {
                var batch = await dbContext.MissingArticleEvents
                    .Where(x => x.JobName == "")
                    .Take(pageSize)
                    .ToListAsync(ct);

                if (batch.Count == 0) break;

                foreach (var evt in batch)
                {
                    evt.JobName = ExtractJobName(evt.Filename);
                }

                await dbContext.SaveChangesAsync(ct);
                processed += batch.Count;
                Log.Information($"Backfilled {processed}/{count} events");
                
                // Yield to allow other operations to proceed (prevent SQLite locking starvation)
                await Task.Delay(500, ct);
            }

            Log.Information("Completed backfilling JobNames");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to backfill JobNames");
        }
    }

    public async Task<(List<MissingArticleItem> Items, int TotalCount)> GetFileSummariesPagedAsync(int page, int pageSize, int totalProviders, string? search = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            var baseQuery = dbContext.MissingArticleEvents.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                baseQuery = baseQuery.Where(x => x.JobName.Contains(search) || x.Filename.Contains(search));
            }

            // 1. Group by File
            var fileGroups = baseQuery.GroupBy(x => new { x.Filename, x.JobName })
                .Select(g => new {
                    g.Key.Filename,
                    g.Key.JobName,
                    MaxDate = g.Max(x => x.Timestamp),
                    TotalEvents = g.Count()
                });

            var totalCount = await fileGroups.CountAsync().ConfigureAwait(false);

            var pagedFiles = await fileGroups
                .OrderByDescending(x => x.MaxDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync()
                .ConfigureAwait(false);

            if (!pagedFiles.Any()) return (new List<MissingArticleItem>(), totalCount);

            var filenames = pagedFiles.Select(x => x.Filename).ToList();

            // 2. Fetch Details for Paged Files
            // We fetch SegmentId and ProviderIndex to calculate stats and blocking status
            var details = await baseQuery
                .Where(x => filenames.Contains(x.Filename))
                .Select(x => new { x.Filename, x.ProviderIndex, x.SegmentId, x.IsImported })
                .ToListAsync()
                .ConfigureAwait(false);

            // 3. Assemble
            var items = pagedFiles.Select(f =>
            {
                var fileEvents = details.Where(d => d.Filename == f.Filename).ToList();
                var providerCounts = fileEvents.GroupBy(x => x.ProviderIndex).ToDictionary(g => g.Key, g => g.Count());
                
                // A file has "blocking" missing articles if ANY segment is missing on ALL providers
                // i.e. Distinct Provider Count for that Segment == Total Providers
                var hasBlocking = fileEvents
                    .GroupBy(x => x.SegmentId)
                    .Any(g => g.Select(p => p.ProviderIndex).Distinct().Count() >= totalProviders);

                return new MissingArticleItem
                {
                    JobName = f.JobName,
                    Filename = f.Filename,
                    LatestTimestamp = f.MaxDate,
                    TotalEvents = f.TotalEvents,
                    ProviderCounts = providerCounts,
                    HasBlockingMissingArticles = hasBlocking,
                    IsImported = fileEvents.Any(x => x.IsImported)
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

    public async Task<(List<MissingArticleEvent> Items, int TotalCount)> GetErrorsPagedAsync(int page, int pageSize, string? search = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            var query = dbContext.MissingArticleEvents.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(x => x.Filename.Contains(search) || x.SegmentId.Contains(search));
            }

            var totalCount = await query.CountAsync().ConfigureAwait(false);

            var items = await query
                .OrderByDescending(x => x.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync()
                .ConfigureAwait(false);

            return (items, totalCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve missing article events");
            return (new List<MissingArticleEvent>(), 0);
        }
    }

    public async Task<List<MissingArticleEvent>> GetErrorsAsync(int limit = 100)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            return await dbContext.MissingArticleEvents
                .AsNoTracking()
                .OrderByDescending(x => x.Timestamp)
                .Take(limit)
                .ToListAsync()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve missing article events");
            return new List<MissingArticleEvent>();
        }
    }

    public async Task ClearErrorsForFile(string filePath)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            await dbContext.MissingArticleEvents
                .Where(x => x.Filename == filePath)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);
                
            Log.Information($"Cleared missing article events for file: {filePath}");
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
            Log.Information("Cleared all missing article events from database");
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