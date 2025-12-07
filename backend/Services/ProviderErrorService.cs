using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public class ProviderErrorService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentQueue<MissingArticleEvent> _buffer = new();
    private readonly Timer _persistenceTimer;

    public ProviderErrorService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _persistenceTimer = new Timer(PersistEvents, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private async void PersistEvents(object? state)
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

    public void RecordError(int providerIndex, string filename, string segmentId, string error)
    {
        _buffer.Enqueue(new MissingArticleEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProviderIndex = providerIndex,
            Filename = filename,
            SegmentId = segmentId,
            Error = error
        });
    }

    public List<MissingArticleEvent> GetErrors(int limit = 100)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            return dbContext.MissingArticleEvents
                .AsNoTracking()
                .OrderByDescending(x => x.Timestamp)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve missing article events");
            return new List<MissingArticleEvent>();
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
}
