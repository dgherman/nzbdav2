using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Logging;

/// <summary>
/// Component-specific logger that respects debug log configuration
/// </summary>
public class ComponentLogger
{
    private readonly string _component;
    private readonly ConfigManager _configManager;

    public ComponentLogger(string component, ConfigManager configManager)
    {
        _component = component;
        _configManager = configManager;
    }

    public void Debug(string message, params object[] args)
    {
        if (_configManager.IsDebugLogEnabled(_component))
        {
            Log.Debug($"[{_component}] {message}", args);
        }
    }

    public void Information(string message, params object[] args)
    {
        Log.Information($"[{_component}] {message}", args);
    }

    public void Warning(string message, params object[] args)
    {
        Log.Warning($"[{_component}] {message}", args);
    }

    public void Warning(Exception ex, string message, params object[] args)
    {
        Log.Warning(ex, $"[{_component}] {message}", args);
    }

    public void Error(string message, params object[] args)
    {
        Log.Error($"[{_component}] {message}", args);
    }

    public void Error(Exception ex, string message, params object[] args)
    {
        Log.Error(ex, $"[{_component}] {message}", args);
    }
}

/// <summary>
/// Available debug log components
/// </summary>
public static class LogComponents
{
    public const string Queue = "queue";
    public const string HealthCheck = "healthcheck";
    public const string BufferedStream = "bufferedstream";
    public const string Analysis = "analysis";
    public const string WebDav = "webdav";
    public const string Usenet = "usenet";
    public const string Database = "database";
    public const string All = "all";

    public static readonly string[] AllComponents = [
        Queue,
        HealthCheck,
        BufferedStream,
        Analysis,
        WebDav,
        Usenet,
        Database
    ];
}
