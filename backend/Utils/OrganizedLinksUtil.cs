using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Note: In this class, a `Link` refers to either a symlink or strm file.
/// </summary>
public static class OrganizedLinksUtil
{
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();
    private static volatile LinkCacheStatus _status = LinkCacheStatus.NotInitialized;
    private static readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly SemaphoreSlim _dbWriteLock = new(1, 1); // Serialize DB writes to prevent SQLite locks
    private static CancellationTokenSource? _refreshCts;
    private static Task? _refreshTask;
    private static IServiceProvider? _serviceProvider;

    public static LinkCacheStatus Status => _status;

    /// <summary>
    /// Adds or updates a link entry in the cache and database.
    /// </summary>
    public static void UpdateCacheEntry(Guid davItemId, string linkPath)
    {
        // Update Memory
        Cache[davItemId] = linkPath;

        // Update DB (with retry logic for SQLite locks)
        if (_serviceProvider != null)
        {
            _ = Task.Run(async () =>
            {
                const int maxRetries = 3;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        await _dbWriteLock.WaitAsync();
                        try
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
                            var existing = db.LocalLinks.FirstOrDefault(x => x.DavItemId == davItemId);
                            if (existing != null)
                            {
                                existing.LinkPath = linkPath;
                            }
                            else
                            {
                                db.LocalLinks.Add(new LocalLink { DavItemId = davItemId, LinkPath = linkPath });
                            }
                            await db.SaveChangesAsync();
                            break; // Success
                        }
                        finally
                        {
                            _dbWriteLock.Release();
                        }
                    }
                    catch (Exception ex) when (i < maxRetries - 1 && ex.Message.Contains("database is locked"))
                    {
                        Log.Warning($"[OrganizedLinksUtil] Database locked on attempt {i + 1}/{maxRetries}, retrying...");
                        await Task.Delay(100 * (i + 1)); // Exponential backoff
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"[OrganizedLinksUtil] Failed to persist link update for {davItemId}");
                        break;
                    }
                }
            });
        }

        Log.Debug($"[OrganizedLinksUtil] Cache updated for DavItem {davItemId} with link {linkPath}");
    }

    /// <summary>
    /// Removes a link entry from the cache and database.
    /// </summary>
    public static void RemoveCacheEntry(Guid davItemId)
    {
        // Update Memory
        Cache.TryRemove(davItemId, out _);

        // Update DB (with retry logic for SQLite locks)
        if (_serviceProvider != null)
        {
            _ = Task.Run(async () =>
            {
                const int maxRetries = 3;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        await _dbWriteLock.WaitAsync();
                        try
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
                            var existing = db.LocalLinks.Where(x => x.DavItemId == davItemId).ToList();
                            if (existing.Any())
                            {
                                db.LocalLinks.RemoveRange(existing);
                                await db.SaveChangesAsync();
                            }
                            break; // Success
                        }
                        finally
                        {
                            _dbWriteLock.Release();
                        }
                    }
                    catch (Exception ex) when (i < maxRetries - 1 && ex.Message.Contains("database is locked"))
                    {
                        Log.Warning($"[OrganizedLinksUtil] Database locked on attempt {i + 1}/{maxRetries}, retrying...");
                        await Task.Delay(100 * (i + 1)); // Exponential backoff
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // Row already deleted by another process/thread - consider success
                        Log.Debug($"[OrganizedLinksUtil] Concurrency exception during removal for {davItemId} - assuming already deleted.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"[OrganizedLinksUtil] Failed to persist link removal for {davItemId}");
                        break;
                    }
                }
            });
        }

        Log.Debug($"[OrganizedLinksUtil] Cache entry removed for DavItem {davItemId}");
    }

    /// <summary>
    /// Starts a background service to periodically refresh the link cache.
    /// </summary>
    public static void StartRefreshService(IServiceProvider serviceProvider, ConfigManager configManager, CancellationToken applicationStoppingToken)
    {
        if (_refreshTask != null) return; // Already running

        _serviceProvider = serviceProvider;
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(applicationStoppingToken);
        _refreshTask = Task.Run(() => RefreshServiceLoop(configManager, _refreshCts.Token), applicationStoppingToken);
        Log.Information("[OrganizedLinksUtil] Background cache refresh service started.");
    }

    /// <summary>
    /// Stops the background refresh service.
    /// </summary>
    public static void StopRefreshService()
    {
        if (_refreshCts == null) return;
        _refreshCts.Cancel();
        _refreshTask?.Wait();
        _refreshCts.Dispose();
        _refreshCts = null;
        _refreshTask = null;
        Log.Information("[OrganizedLinksUtil] Background cache refresh service stopped.");
    }

    private static async Task RefreshServiceLoop(ConfigManager configManager, CancellationToken ct)
    {
        // Initial load from DB (Fast)
        await InitializeFromDbAsync(ct);
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Full Sync (Disk Scan)
                await SyncLinksAsync(configManager, ct);
                
                // Refresh every 5 minutes
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); 
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[OrganizedLinksUtil] Error during periodic cache refresh.");
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task InitializeFromDbAsync(CancellationToken ct)
    {
        if (_serviceProvider == null) return;
        
        Log.Information("[OrganizedLinksUtil] Loading link cache from database...");
        _status = LinkCacheStatus.Initializing;
        
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var links = await db.LocalLinks.AsNoTracking().ToListAsync(ct);
            
            foreach (var link in links)
            {
                Cache[link.DavItemId] = link.LinkPath;
            }
            
            _status = LinkCacheStatus.Initialized;
            Log.Information($"[OrganizedLinksUtil] Link cache loaded from database with {links.Count} links.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[OrganizedLinksUtil] Failed to load link cache from database.");
            _status = LinkCacheStatus.Error;
        }
    }

    private static async Task SyncLinksAsync(ConfigManager configManager, CancellationToken ct)
    {
        if (_serviceProvider == null) return;
        
        Log.Information("[OrganizedLinksUtil] Starting filesystem sync...");
        
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // 1. Scan Disk (Heavy operation)
            var rawDiskLinks = await Task.Run(() => GetLibraryDavItemLinks(configManager).ToList(), ct);

            // Validate FKs: Only keep links that point to existing DavItems
            var davItemIds = rawDiskLinks.Select(x => x.DavItemId).Distinct().ToList();
            var validDavItemIds = await dbContext.Items
                .AsNoTracking()
                .Where(x => davItemIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(ct);
            var validDavItemIdSet = new HashSet<Guid>(validDavItemIds);

            var onDiskLinks = rawDiskLinks.Where(x => validDavItemIdSet.Contains(x.DavItemId)).ToList();
            var onDiskMap = onDiskLinks.ToDictionary(x => x.LinkPath, x => x.DavItemId);

            // 2. Load DB
            var dbLinks = await dbContext.LocalLinks.ToListAsync(ct);
            var dbMap = dbLinks.ToDictionary(x => x.LinkPath, x => x);

            // 3. Compare
            var toAdd = new List<LocalLink>();
            var toRemove = new List<LocalLink>();
            var toUpdate = new List<LocalLink>();

            foreach (var diskLink in onDiskLinks)
            {
                if (dbMap.TryGetValue(diskLink.LinkPath, out var dbLink))
                {
                    if (dbLink.DavItemId != diskLink.DavItemId)
                    {
                        dbLink.DavItemId = diskLink.DavItemId;
                        toUpdate.Add(dbLink);
                    }
                }
                else
                {
                    toAdd.Add(new LocalLink
                    {
                        LinkPath = diskLink.LinkPath,
                        DavItemId = diskLink.DavItemId
                    });
                }
            }

            foreach (var dbLink in dbLinks)
            {
                if (!onDiskMap.ContainsKey(dbLink.LinkPath))
                {
                    toRemove.Add(dbLink);
                }
            }

            // 4. Update Memory Cache
            foreach (var link in toAdd) Cache[link.DavItemId] = link.LinkPath;
            foreach (var link in toUpdate) Cache[link.DavItemId] = link.LinkPath;
            foreach (var link in toRemove) Cache.TryRemove(link.DavItemId, out _);

            // 5. Update DB
            if (toAdd.Count > 0) dbContext.LocalLinks.AddRange(toAdd);
            if (toRemove.Count > 0) dbContext.LocalLinks.RemoveRange(toRemove);
            // Updates are tracked by EF

            if (toAdd.Count > 0 || toRemove.Count > 0 || toUpdate.Count > 0)
            {
                await dbContext.SaveChangesAsync(ct);
                Log.Information($"[OrganizedLinksUtil] Sync complete. Added: {toAdd.Count}, Removed: {toRemove.Count}, Updated: {toUpdate.Count}");
            }
            else
            {
                Log.Information("[OrganizedLinksUtil] Sync complete. No changes detected.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[OrganizedLinksUtil] Error during filesystem sync.");
        }
    }

    // Kept for compatibility but now just ensures DB load
    public static async Task InitializeAsync(ConfigManager configManager)
    {
        if (_status == LinkCacheStatus.Initialized) return;
        await InitializeFromDbAsync(CancellationToken.None);
    }

    public static string? GetLink(DavItem targetDavItem, ConfigManager configManager, bool allowScan = true)
    {
        if (Cache.TryGetValue(targetDavItem.Id, out var link) && Verify(link, targetDavItem, configManager))
        {
            return link;
        }
        
        // Scan fallback is no longer supported/needed as we sync in background
        // But for compatibility we can return null if not in cache
        return null;
    }

    public static IEnumerable<DavItemLink> GetLibraryDavItemLinks(ConfigManager configManager)
    {
        var libraryRoot = configManager.GetLibraryDir()!;
        var allSymlinksAndStrms = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(libraryRoot);
        return GetDavItemLinks(allSymlinksAndStrms, configManager);
    }

    private static bool Verify(string linkFromCache, DavItem targetDavItem, ConfigManager configManager)
    {
        var mountDir = configManager.GetRcloneMountDir();
        var fileInfo = new FileInfo(linkFromCache);
        if (!fileInfo.Exists) return false; // Basic check

        var symlinkOrStrmInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfo);
        if (symlinkOrStrmInfo == null) return false;
        var davItemLink = GetDavItemLink(symlinkOrStrmInfo, mountDir);
        return davItemLink?.DavItemId == targetDavItem.Id;
    }

    private static string? SearchForLink(DavItem targetDavItem, ConfigManager configManager)
    {
        // Deprecated
        return null;
    }

    private static IEnumerable<DavItemLink> GetDavItemLinks
    (
        IEnumerable<SymlinkAndStrmUtil.ISymlinkOrStrmInfo> symlinkOrStrmInfos,
        ConfigManager configManager
    )
    {
        var mountDir = configManager.GetRcloneMountDir();
        return symlinkOrStrmInfos
            .Select(x => GetDavItemLink(x, mountDir))
            .Where(x => x != null)
            .Select(x => x!.Value);
    }

    public static DavItemLink? GetDavItemLink
    (
        SymlinkAndStrmUtil.ISymlinkOrStrmInfo symlinkOrStrmInfo,
        string mountDir
    )
    {
        return symlinkOrStrmInfo switch
        {
            SymlinkAndStrmUtil.SymlinkInfo symlinkInfo => GetDavItemLink(symlinkInfo, mountDir),
            SymlinkAndStrmUtil.StrmInfo strmInfo => GetDavItemLink(strmInfo),
            _ => throw new Exception("Unknown link type")
        };
    }

    private static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.SymlinkInfo symlinkInfo, string mountDir)
    {
        var targetPath = symlinkInfo.TargetPath;
        if (!targetPath.StartsWith(mountDir)) return null;
        targetPath = targetPath.RemovePrefix(mountDir);
        targetPath = targetPath.StartsWith('/') ? targetPath : $"/{targetPath}";
        if (!targetPath.StartsWith("/.ids")) return null;
        var guid = Path.GetFileNameWithoutExtension(targetPath);
        if (!Guid.TryParse(guid, out var result)) return null;
        return new DavItemLink()
        {
            LinkPath = symlinkInfo.SymlinkPath,
            DavItemId = result,
            SymlinkOrStrmInfo = symlinkInfo
        };
    }

    private static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.StrmInfo strmInfo)
    {
        var targetUrl = strmInfo.TargetUrl;
        var absolutePath = new Uri(targetUrl).AbsolutePath;
        if (!absolutePath.StartsWith("/view/.ids")) return null;
        var guid = Path.GetFileNameWithoutExtension(absolutePath);
        if (!Guid.TryParse(guid, out var result)) return null;
        return new DavItemLink()
        {
            LinkPath = strmInfo.StrmPath,
            DavItemId = result,
            SymlinkOrStrmInfo = strmInfo
        };
    }
    
    public static async Task<(List<MappedFile> Items, int TotalCount)> GetMappedFilesPagedAsync(
        DavDatabaseContext dbContext, ConfigManager configManager, int page, int pageSize, string? search = null)
    {
        // Join LocalLinks with Items table upfront to enable searching on all fields
        var query = from link in dbContext.LocalLinks.AsNoTracking()
                    join item in dbContext.Items.AsNoTracking() on link.DavItemId equals item.Id into itemGroup
                    from item in itemGroup.DefaultIfEmpty()
                    select new
                    {
                        link.DavItemId,
                        link.LinkPath,
                        link.CreatedAt,
                        DavItemPath = item != null ? item.Path : null
                    };

        // Apply search filter across all columns (DavItemId, LinkPath, DavItemPath)
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.ToLower();
            query = query.Where(x =>
                x.DavItemId.ToString().ToLower().Contains(search) ||
                x.LinkPath.ToLower().Contains(search) ||
                (x.DavItemPath != null && x.DavItemPath.ToLower().Contains(search))
            );
        }

        var totalCount = await query.CountAsync();

        var results = await query
            .OrderBy(x => x.LinkPath)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // Map to MappedFile and enrich with scene name and target path
        var items = new List<MappedFile>();
        foreach (var result in results)
        {
            var item = new MappedFile
            {
                DavItemId = result.DavItemId,
                LinkPath = result.LinkPath,
                CreatedAt = result.CreatedAt,
                DavItemPath = result.DavItemPath
            };

            // Extract Scene Name from DavItemPath
            if (!string.IsNullOrEmpty(result.DavItemPath))
            {
                var parts = result.DavItemPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    item.DavItemName = parts[2]; // Scene Name
                }
                else
                {
                    item.DavItemName = Path.GetFileName(item.LinkPath);
                }
            }
            else
            {
                item.DavItemName = Path.GetFileName(item.LinkPath);
            }

            // Calculate Target Path/URL by reading the file
            item.TargetPath = GetSymlinkOrStrmTarget(item.LinkPath, configManager);

            items.Add(item);
        }

        return (items, totalCount);
    }
    
    private static string GetSymlinkOrStrmTarget(string linkPath, ConfigManager configManager)
    {
        try 
        {
            var fileInfo = new FileInfo(linkPath);
            if (!fileInfo.Exists) return "File Not Found";

            var symlinkOrStrmInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfo);

            if (symlinkOrStrmInfo is SymlinkAndStrmUtil.SymlinkInfo symInfo)
            {
                return symInfo.TargetPath;
            }
            else if (symlinkOrStrmInfo is SymlinkAndStrmUtil.StrmInfo strmInfo)
            {
                return strmInfo.TargetUrl;
            }
        }
        catch {}
        return "Error";
    }

    public struct DavItemLink
    {
        public string LinkPath; // Path to either a symlink or strm file.
        public Guid DavItemId;
        public SymlinkAndStrmUtil.ISymlinkOrStrmInfo SymlinkOrStrmInfo;
    }
}

public enum LinkCacheStatus
{
    NotInitialized,
    Initializing,
    Initialized,
    Error
}