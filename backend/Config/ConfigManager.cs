using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog.Events;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    public string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    public T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue);
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }

            OnConfigChanged?.Invoke(this, new ConfigEventArgs
            {
                ChangedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue),
                NewConfig = _config
            });
        }
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("MOUNT_DIR"))
               ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    /// <summary>
    /// Gets the static download key for WebDAV /view/ downloads.
    /// Auto-generates one if not present.
    /// </summary>
    public string GetStaticDownloadKey()
    {
        var key = GetConfigValue("webdav.static-download-key");
        if (!string.IsNullOrEmpty(key)) return key;

        // Auto-generate a new key
        key = GenerateNewDownloadKey();
        _ = SaveStaticDownloadKeyAsync(key);
        return key;
    }

    /// <summary>
    /// Regenerates the static download key
    /// </summary>
    public async Task<string> RegenerateStaticDownloadKeyAsync()
    {
        var newKey = GenerateNewDownloadKey();
        await SaveStaticDownloadKeyAsync(newKey).ConfigureAwait(false);
        return newKey;
    }

    private static string GenerateNewDownloadKey()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexStringLower(bytes);
    }

    private async Task SaveStaticDownloadKeyAsync(string key)
    {
        await using var db = new DavDatabaseContext();
        var existing = await db.ConfigItems.FirstOrDefaultAsync(c => c.ConfigName == "webdav.static-download-key").ConfigureAwait(false);
        if (existing != null)
        {
            existing.ConfigValue = key;
        }
        else
        {
            db.ConfigItems.Add(new ConfigItem { ConfigName = "webdav.static-download-key", ConfigValue = key });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Update in-memory cache
        UpdateValues([new ConfigItem { ConfigName = "webdav.static-download-key", ConfigValue = key }]);
    }

    public string GetApiCategories()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.categories"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("CATEGORIES"))
               ?? "audio,software,tv,movies";
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    // Default User-Agent for the SABnzbd `addurl` api when fetching NZB files over HTTP.
    // A generic browser string is used by default so the request is not flagged as a
    // usenet streamer / nzbdav by indexers that are leery of them. Override via the
    // `api.user-agent` config value or the NZB_GRAB_USER_AGENT environment variable
    // (e.g. set a SABnzbd or NZBGet string for indexers that expect a known client).
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/134.0.6998.166 Safari/537.36";

    public string GetUserAgent()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("NZB_GRAB_USER_AGENT"))
               ?? DefaultUserAgent;
    }

    public int GetConnectionsPerStream()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.connections-per-stream"))
            ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("CONNECTIONS_PER_STREAM"))
            ?? "20"  // Increased default - this is now workers per stream (not global limit)
        );
    }

    /// <summary>
    /// Gets the total number of streaming connections shared across all active streams.
    /// With 1 stream active, it gets all connections. With 2 streams, each gets half, etc.
    /// </summary>
    public int GetTotalStreamingConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.total-streaming-connections"))
            ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("TOTAL_STREAMING_CONNECTIONS"))
            ?? "20"
        );
    }

    public bool UseBufferedStreaming()
    {
        return bool.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.use-buffered-streaming"))
            ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("USE_BUFFERED_STREAMING"))
            ?? "true"
        );
    }

    public int GetStreamBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.stream-buffer-size"))
            ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("STREAM_BUFFER_SIZE"))
            ?? "20"
        );
    }

    public int GetMaxConcurrentBufferedStreams()
    {
        // Default 8 (was 2): a single multipart/RAR playback needs a buffered-stream slot per active
        // part, plus the player's parallel head/tail probes — 2 caused "No semaphore slot available"
        // and stalls. Each slot holds a ~32 MB ring buffer (see shared-stream buffer size).
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.max-concurrent-buffered-streams"))
            ?? "8"
        );
    }

    public int GetSharedStreamGracePeriod()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.shared-stream-grace-period"))
            ?? "10"
        );
    }

    public int GetSharedStreamBufferSize()
    {
        var mb = int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.shared-stream-buffer-size"))
            ?? "32"
        );
        return Math.Max(2, mb) * 1024 * 1024; // Convert MB to bytes, minimum 2MB
    }

    public LogEventLevel? GetLogLevel()
    {
        var val = GetConfigValue("general.log-level");
        if (Enum.TryParse<LogEventLevel>(val, true, out var level))
            return level;
        return null;
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("WEBDAV_USER"))
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = Environment.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableMediaEnabled()
    {
        var defaultValue = true;
        // Check new config key first, fall back to legacy key for backward compatibility
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-media"))
                          ?? StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxQueueConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("api.max-queue-connections"))
            ?? "1" // Default to 1 to maximize streaming connections
        );
    }

    public int GetStreamingReserve()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-reserve"))
            ?? "5"
        );
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public int GetMaxDownloadConnections()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"));
        if (stringValue != null)
        {
            return int.Parse(stringValue);
        }
        var providerConfig = GetUsenetProviderConfig();
        return Math.Min(providerConfig.TotalPooledConnections, 15);
    }

    /// <summary>
    /// Gets the number of connections that should be reserved for queue processing.
    /// All non-queue operations (streaming, health checks) should set this as their
    /// GlobalOperationLimiter enforces this limit globally across all providers.
    /// </summary>
    public int GetReservedConnectionsForQueue()
    {
        var providerConfig = GetUsenetProviderConfig();
        var maxQueueConnections = GetMaxQueueConnections();
        return Math.Max(0, providerConfig.TotalPooledConnections - maxQueueConnections);
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsEnsureArticleExistenceEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-article-existence"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public int GetHistoryRetentionHours()
    {
        var defaultValue = 24;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.history-retention-hours"));
        return (configValue != null ? int.Parse(configValue) : defaultValue);
    }

    public int GetMaxRepairConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("repair.connections"))
            ?? "1" // Default to 1 to maximize streaming connections
        );
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetMaxRepairConnections() > 0
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public int GetMinHealthCheckIntervalDays()
    {
        var defaultValue = 7; // Default to 7 days minimum interval
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.min-check-interval-days"));
        return configValue != null ? int.Parse(configValue) : defaultValue;
    }

    public bool IsAnalysisEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("analysis.enable"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public int GetMaxConcurrentAnalyses()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("analysis.max-concurrent"))
            ?? "3"
        );
    }

    public bool IsProviderAffinityEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("provider-affinity.enable"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsStatsEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("stats.enable"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool HideSamples()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("usenet.hide-samples"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public int GetUsenetOperationTimeout()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.operation-timeout"))
            ?? StringUtil.EmptyToNull(Environment.GetEnvironmentVariable("USENET_OPERATION_TIMEOUT"))
            ?? "180" // Increased from 90s to 180s for large NZBs and slower providers
        );
    }

    public HashSet<string> GetDebugLogComponents()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue("debug.components"));
        if (configValue == null) return [];

        try
        {
            var components = JsonSerializer.Deserialize<List<string>>(configValue);
            return components?.ToHashSet() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public bool IsDebugLogEnabled(string component)
    {
        var components = GetDebugLogComponents();
        return components.Contains(component) || components.Contains("all");
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlacklistedExtensions()
    {
        var defaultValue = ".nfo, .par2, .sfv";
        return (GetConfigValue("api.download-extension-blacklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public bool IsStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.startup-vacuum"));
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public List<string> GetHealthCheckCategories()
    {
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.health-check-categories"));
        if (configValue == null) return [];

        return configValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public RcloneRcConfig GetRcloneRcConfig()
    {
        var defaultValue = new RcloneRcConfig();
        return GetConfigValue<RcloneRcConfig>("rclone.rc") ?? defaultValue;
    }

    public class ConfigEventArgs : EventArgs
    {
        public Dictionary<string, string> ChangedConfig { get; set; } = new();
        public Dictionary<string, string> NewConfig { get; set; } = new();
    }
}