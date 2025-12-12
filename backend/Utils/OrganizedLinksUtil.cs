using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
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

    public static LinkCacheStatus Status => _status;

    /// <summary>
    /// Initializes the link cache by scanning the library asynchronously.
    /// </summary>
    public static async Task InitializeAsync(ConfigManager configManager)
    {
        if (_status != LinkCacheStatus.NotInitialized) return;
        
        Log.Information("[OrganizedLinksUtil] Starting link cache initialization...");
        _status = LinkCacheStatus.Initializing;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_status != LinkCacheStatus.Initializing) return; // Re-check if status changed while waiting for lock

            // The actual heavy work is done here
            await Task.Run(() =>
            {
                var scannedLinks = 0;
                foreach (var davItemLink in GetLibraryDavItemLinks(configManager))
                {
                    Cache[davItemLink.DavItemId] = davItemLink.LinkPath;
                    scannedLinks++;
                }
                Log.Information($"[OrganizedLinksUtil] Link cache initialized with {scannedLinks} links.");
            }).ConfigureAwait(false);
            _status = LinkCacheStatus.Initialized;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[OrganizedLinksUtil] Error initializing link cache.");
            _status = LinkCacheStatus.Error;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Searches organized media library for a symlink or strm pointing to the given target
    /// </summary>
    /// <param name="targetDavItem">The given target</param>
    /// <param name="configManager">The application config</param>
    /// <param name="allowScan">If true, allows scanning the filesystem on cache miss (slow). If false, returns null on cache miss.</param>
    /// <returns>The path to a symlink or strm in the organized media library that points to the given target.</returns>
    public static string? GetLink(DavItem targetDavItem, ConfigManager configManager, bool allowScan = true)
    {
        if (Cache.TryGetValue(targetDavItem.Id, out var link) && Verify(link, targetDavItem, configManager))
        {
            return link;
        }

        // If cache is not initialized yet, we must not block by scanning for the IsImported status in HealthCheck.
        // Repair logic can still trigger a scan because it's less frequent and needs accuracy.
        if (!allowScan && (_status == LinkCacheStatus.NotInitialized || _status == LinkCacheStatus.Initializing))
        {
            return null;
        }

        return allowScan ? SearchForLink(targetDavItem, configManager) : null;
    }

    /// <summary>
    /// Enumerates all DavItemLinks within the organized media library that point to nzbdav dav-items.
    /// </summary>
    /// <param name="configManager">The application config</param>
    /// <returns>All DavItemLinks within the organized media library that point to nzbdav dav-items.</returns>
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
        var symlinkOrStrmInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfo);
        if (symlinkOrStrmInfo == null) return false;
        var davItemLink = GetDavItemLink(symlinkOrStrmInfo, mountDir);
        return davItemLink?.DavItemId == targetDavItem.Id;
    }

    private static string? SearchForLink(DavItem targetDavItem, ConfigManager configManager)
    {
        string? result = null;
        foreach (var davItemLink in GetLibraryDavItemLinks(configManager))
        {
            Cache[davItemLink.DavItemId] = davItemLink.LinkPath;
            if (davItemLink.DavItemId == targetDavItem.Id)
                result = davItemLink.LinkPath;
        }

        return result;
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

    private static DavItemLink? GetDavItemLink
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
        return new DavItemLink()
        {
            LinkPath = symlinkInfo.SymlinkPath,
            DavItemId = Guid.Parse(guid),
            SymlinkOrStrmInfo = symlinkInfo
        };
    }

    private static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.StrmInfo strmInfo)
    {
        var targetUrl = strmInfo.TargetUrl;
        var absolutePath = new Uri(targetUrl).AbsolutePath;
        if (!absolutePath.StartsWith("/view/.ids")) return null;
        var guid = Path.GetFileNameWithoutExtension(absolutePath);
        return new DavItemLink()
        {
            LinkPath = strmInfo.StrmPath,
            DavItemId = Guid.Parse(guid),
            SymlinkOrStrmInfo = strmInfo
        };
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
