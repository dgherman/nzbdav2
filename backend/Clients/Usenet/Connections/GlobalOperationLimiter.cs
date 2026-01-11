using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Global operation limiter that enforces per-operation limits with QoS-style ballooning
/// across ALL providers (not per-provider).
/// </summary>
public class GlobalOperationLimiter : IDisposable
{
    private readonly Dictionary<ConnectionUsageType, int> _guaranteedLimits;
    private readonly Dictionary<ConnectionUsageType, int> _currentUsage = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _queueSemaphore;
    private readonly SemaphoreSlim _healthCheckSemaphore;
    private readonly SemaphoreSlim _streamingSemaphore;
    private readonly ConfigManager? _configManager;

    public GlobalOperationLimiter(
        int maxQueueConnections,
        int maxHealthCheckConnections,
        int totalConnections,
        ConfigManager? configManager = null)
    {
        _configManager = configManager;

        // Ensure values are at least 1 to prevent SemaphoreSlim errors
        maxQueueConnections = Math.Max(1, maxQueueConnections);
        maxHealthCheckConnections = Math.Max(1, maxHealthCheckConnections);

        var maxStreamingConnections = Math.Max(1, totalConnections - maxQueueConnections - maxHealthCheckConnections);

        _guaranteedLimits = new Dictionary<ConnectionUsageType, int>
        {
            { ConnectionUsageType.Queue, maxQueueConnections },
            { ConnectionUsageType.HealthCheck, maxHealthCheckConnections },
            { ConnectionUsageType.Streaming, maxStreamingConnections },
            { ConnectionUsageType.BufferedStreaming, maxStreamingConnections },
            { ConnectionUsageType.Repair, maxHealthCheckConnections }, // Share with HealthCheck
            { ConnectionUsageType.Analysis, maxHealthCheckConnections }, // Share with HealthCheck
            { ConnectionUsageType.Unknown, maxStreamingConnections }
        };

        // Initialize usage tracking
        foreach (var type in _guaranteedLimits.Keys)
        {
            _currentUsage[type] = 0;
        }

        // Create semaphores for guaranteed limits (these never change)
        _queueSemaphore = new SemaphoreSlim(maxQueueConnections, maxQueueConnections);
        _healthCheckSemaphore = new SemaphoreSlim(maxHealthCheckConnections, maxHealthCheckConnections);
        _streamingSemaphore = new SemaphoreSlim(maxStreamingConnections, maxStreamingConnections);

        // Serilog.Log.Information($"[GlobalOperationLimiter] Initialized: Queue={maxQueueConnections}, HealthCheck={maxHealthCheckConnections}, Streaming={maxStreamingConnections}, Total={totalConnections}");
    }

    /// <summary>
    /// Acquires a permit for the given operation type. Must be released via ReleasePermit.
    /// CRITICAL: This method respects cancellation tokens to prevent deadlocks when tasks timeout.
    /// </summary>
    public async Task<OperationPermit> AcquirePermitAsync(ConnectionUsageType usageType, CancellationToken cancellationToken = default)
    {
        var semaphore = GetSemaphoreForType(usageType);
        var guaranteedLimit = _guaranteedLimits[usageType];

        // Extract context to get file/job details
        var context = cancellationToken.GetContext<ConnectionUsageContext>();
        var fileDetails = context.Details;

        LogDebugForType(usageType, "Requesting permit for {UsageType}. Current usage: {UsageBreakdown}. Semaphore available: {SemaphoreAvailable}",
            usageType, GetUsageBreakdown(), semaphore.CurrentCount);

        var waitStartTime = DateTime.UtcNow;

        // Wait for the operation-specific semaphore - MUST respect cancellation token to prevent deadlocks!
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        var waitElapsed = DateTime.UtcNow - waitStartTime;

        // Track usage
        int currentUsage;
        lock (_lock)
        {
            _currentUsage[usageType]++;
            currentUsage = _currentUsage[usageType];
        }

        if (waitElapsed.TotalSeconds > 2)
        {
            if (fileDetails != null)
            {
                Serilog.Log.Debug("[GlobalPool] Acquired permit for {UsageType} after waiting {WaitSeconds}s. File: {FileDetails}. Current usage: {CurrentUsage}/{GuaranteedLimit}. Total: {UsageBreakdown}",
                    usageType, waitElapsed.TotalSeconds, fileDetails, currentUsage, guaranteedLimit, GetUsageBreakdown());
            }
            else
            {
                Serilog.Log.Debug("[GlobalPool] Acquired permit for {UsageType} after waiting {WaitSeconds}s. Current usage: {CurrentUsage}/{GuaranteedLimit}. Total: {UsageBreakdown}",
                    usageType, waitElapsed.TotalSeconds, currentUsage, guaranteedLimit, GetUsageBreakdown());
            }
        }
        else
        {
            if (fileDetails != null)
            {
                LogDebugForType(usageType, "Acquired permit for {UsageType}. File: {FileDetails}. Current usage: {CurrentUsage}/{GuaranteedLimit}. Total: {UsageBreakdown}",
                    usageType, fileDetails, currentUsage, guaranteedLimit, GetUsageBreakdown());
            }
            else
            {
                LogDebugForType(usageType, "Acquired permit for {UsageType}. Current usage: {CurrentUsage}/{GuaranteedLimit}. Total: {UsageBreakdown}",
                    usageType, currentUsage, guaranteedLimit, GetUsageBreakdown());
            }
        }

        return new OperationPermit(this, usageType, semaphore, DateTime.UtcNow, fileDetails);
    }

    private void ReleasePermit(ConnectionUsageType usageType, SemaphoreSlim semaphore, DateTime acquiredAt, string? fileDetails)
    {
        var heldDuration = DateTime.UtcNow - acquiredAt;

        int currentUsage;
        lock (_lock)
        {
            if (_currentUsage.ContainsKey(usageType) && _currentUsage[usageType] > 0)
            {
                _currentUsage[usageType]--;
            }
            else
            {
                Serilog.Log.Error("[GlobalPool] CRITICAL: Attempted to release permit for {UsageType} but usage counter is already 0! This indicates a double-release bug.",
                    usageType);
            }
            currentUsage = _currentUsage[usageType];
        }

        semaphore.Release();

        if (heldDuration.TotalMinutes > 5)
        {
            if (fileDetails != null)
            {
                Serilog.Log.Warning("[GlobalPool] Released permit for {UsageType} after holding for {HeldMinutes:F1} minutes. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalMinutes, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                Serilog.Log.Warning("[GlobalPool] Released permit for {UsageType} after holding for {HeldMinutes:F1} minutes. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalMinutes, currentUsage, GetUsageBreakdown());
            }
        }
        else if (heldDuration.TotalSeconds > 30)
        {
            if (fileDetails != null)
            {
                LogInfoForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                LogInfoForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, currentUsage, GetUsageBreakdown());
            }
        }
        else
        {
            if (fileDetails != null)
            {
                LogDebugForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                LogDebugForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, currentUsage, GetUsageBreakdown());
            }
        }
    }

    private SemaphoreSlim GetSemaphoreForType(ConnectionUsageType type)
    {
        return type switch
        {
            ConnectionUsageType.Queue => _queueSemaphore,
            ConnectionUsageType.HealthCheck => _healthCheckSemaphore,
            ConnectionUsageType.Repair => _healthCheckSemaphore, // Share with HealthCheck
            ConnectionUsageType.Analysis => _healthCheckSemaphore, // Share with HealthCheck
            ConnectionUsageType.Streaming => _streamingSemaphore,
            ConnectionUsageType.BufferedStreaming => _streamingSemaphore,
            _ => _streamingSemaphore
        };
    }

    private string GetUsageBreakdown()
    {
        lock (_lock)
        {
            var parts = _currentUsage
                .Where(kvp => kvp.Value > 0)
                .Select(kvp => $"{kvp.Key}={kvp.Value}")
                .ToArray();
            return parts.Length > 0 ? string.Join(",", parts) : "none";
        }
    }

    private void LogDebugForType(ConnectionUsageType usageType, string message, params object[] args)
    {
        if (_configManager == null)
        {
            Serilog.Log.Debug("[GlobalPool] " + message, args);
            return;
        }

        var component = GetComponentForType(usageType);
        if (_configManager.IsDebugLogEnabled(component))
        {
            Serilog.Log.Debug("[GlobalPool] " + message, args);
        }
    }

    private void LogInfoForType(ConnectionUsageType usageType, string message, params object[] args)
    {
        // HealthCheck and Streaming operations should log at Debug level to reduce noise
        if (usageType == ConnectionUsageType.HealthCheck || 
            usageType == ConnectionUsageType.Repair || 
            usageType == ConnectionUsageType.Analysis ||
            usageType == ConnectionUsageType.Streaming ||
            usageType == ConnectionUsageType.BufferedStreaming)
        {
            LogDebugForType(usageType, message, args);
            return;
        }

        // Information logs should ALWAYS show regardless of component debug settings
        // Only Debug logs are filtered by component
        Log.Information("[GlobalPool] " + message, args);
    }

    private static string GetComponentForType(ConnectionUsageType usageType)
    {
        return usageType switch
        {
            ConnectionUsageType.Queue => LogComponents.Queue,
            ConnectionUsageType.HealthCheck => LogComponents.HealthCheck,
            ConnectionUsageType.Repair => LogComponents.HealthCheck,
            ConnectionUsageType.Analysis => LogComponents.Analysis,
            ConnectionUsageType.Streaming => LogComponents.BufferedStream,
            ConnectionUsageType.BufferedStreaming => LogComponents.BufferedStream,
            _ => LogComponents.Usenet
        };
    }

    public void Dispose()
    {
        _queueSemaphore.Dispose();
        _healthCheckSemaphore.Dispose();
        _streamingSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Represents a permit to perform an operation. Must be disposed to release the permit.
    /// CRITICAL: This is a class (reference type) to prevent struct copying issues that cause permit leaks.
    /// </summary>
    public sealed class OperationPermit : IDisposable
    {
        private readonly GlobalOperationLimiter _limiter;
        private readonly ConnectionUsageType _usageType;
        private readonly SemaphoreSlim _semaphore;
        private readonly DateTime _acquiredAt;
        private readonly string? _fileDetails;
        private int _disposed; // 0 = not disposed, 1 = disposed

        internal OperationPermit(GlobalOperationLimiter limiter, ConnectionUsageType usageType, SemaphoreSlim semaphore, DateTime acquiredAt, string? fileDetails)
        {
            _limiter = limiter;
            _usageType = usageType;
            _semaphore = semaphore;
            _acquiredAt = acquiredAt;
            _fileDetails = fileDetails;
        }

        public void Dispose()
        {
            // Thread-safe dispose guard: only release permit once
            // Prevents double-dispose bugs that caused Queue permit leaks
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _limiter.ReleasePermit(_usageType, _semaphore, _acquiredAt, _fileDetails);
            }
        }
    }
}
