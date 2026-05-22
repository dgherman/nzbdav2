// backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Global operation limiter using a shared PrioritizedSemaphore with streaming reserve.
/// Streaming (High priority) always has guaranteed slots. Queue (Low priority) can use
/// the full pool when idle, yields gracefully under contention.
/// </summary>
public class GlobalOperationLimiter : IDisposable
{
    private readonly PrioritizedSemaphore _sharedPool;
    private readonly SemaphoreSlim _lowPriorityGate;
    private readonly Dictionary<ConnectionUsageType, int> _currentUsage = new();
    private readonly object _lock = new();
    private readonly int _totalConnections;
    private readonly int _streamingReserve;
    private readonly ConfigManager? _configManager;

    public GlobalOperationLimiter(
        int totalConnections,
        int streamingReserve,
        SemaphorePriorityOdds priorityOdds,
        ConfigManager? configManager = null)
    {
        _configManager = configManager;
        // Floor at 2 so there is always room for at least 1 streaming reserve slot
        // AND 1 low-priority slot. With totalConnections <= 1 (e.g. fresh start with no
        // provider configured), lowPriorityMax would compute to 0 and crash the
        // SemaphoreSlim ctor ("maximumCount argument must be a positive number"),
        // taking down the backend before the config page can be served.
        _totalConnections = Math.Max(2, totalConnections);
        _streamingReserve = Math.Max(1, Math.Min(streamingReserve, _totalConnections - 1));

        _sharedPool = new PrioritizedSemaphore(_totalConnections, _totalConnections, priorityOdds);

        // Low-priority gate: allows up to (total - reserve) concurrent low-priority operations.
        // This guarantees that 'streamingReserve' slots are always available for High-priority.
        var lowPriorityMax = Math.Max(1, _totalConnections - _streamingReserve);
        _lowPriorityGate = new SemaphoreSlim(lowPriorityMax, lowPriorityMax);

        // Initialize usage tracking for all known types
        foreach (var type in Enum.GetValues<ConnectionUsageType>())
        {
            _currentUsage[type] = 0;
        }

        Log.Information("[GlobalPool] Initialized: TotalConnections={Total}, StreamingReserve={Reserve}, LowPriorityMax={LowMax}, StreamingPriority={Priority}%",
            _totalConnections, _streamingReserve, lowPriorityMax, priorityOdds.HighPriorityOdds);
    }

    // Backwards-compatible constructor for callers that haven't been updated yet
    public GlobalOperationLimiter(
        int maxQueueConnections,
        int maxHealthCheckConnections,
        int totalConnections,
        ConfigManager? configManager = null)
        : this(
            totalConnections,
            streamingReserve: configManager?.GetStreamingReserve() ?? 5,
            priorityOdds: configManager?.GetStreamingPriority() ?? new SemaphorePriorityOdds { HighPriorityOdds = 80 },
            configManager)
    {
        // Log deprecation if max-queue-connections was explicitly set
        if (configManager != null && maxQueueConnections != 1)
        {
            Log.Warning("[GlobalPool] api.max-queue-connections is deprecated. Queue now shares the full connection pool with priority-based scheduling. " +
                        "Use usenet.streaming-reserve (default 5) and usenet.streaming-priority (default 80) instead.");
        }
    }

    /// <summary>
    /// Acquires a permit for the given operation type. Must be released via OperationPermit.Dispose().
    /// </summary>
    public async Task<OperationPermit> AcquirePermitAsync(ConnectionUsageType usageType, CancellationToken cancellationToken = default)
    {
        var priority = GetPriorityForType(usageType);

        var context = cancellationToken.GetContext<ConnectionUsageContext>();
        var fileDetails = context.Details;

        LogDebugForType(usageType, "Requesting permit for {UsageType}. Priority: {Priority}. Current usage: {UsageBreakdown}",
            usageType, priority, GetUsageBreakdown());

        var waitStartTime = DateTime.UtcNow;

        // Low-priority callers must first acquire the reserve gate to ensure
        // streaming always has 'streamingReserve' slots available
        bool acquiredLowPriorityGate = false;
        if (priority == SemaphorePriority.Low)
        {
            await _lowPriorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquiredLowPriorityGate = true;
        }

        try
        {
            // Acquire from the shared pool with appropriate priority
            await _sharedPool.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If shared pool acquisition fails (cancellation), release the low-priority gate
            if (acquiredLowPriorityGate)
                _lowPriorityGate.Release();
            throw;
        }

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
                Log.Debug("[GlobalPool] Acquired permit for {UsageType} after waiting {WaitSeconds:F1}s. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, waitElapsed.TotalSeconds, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                Log.Debug("[GlobalPool] Acquired permit for {UsageType} after waiting {WaitSeconds:F1}s. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, waitElapsed.TotalSeconds, currentUsage, GetUsageBreakdown());
            }
        }
        else
        {
            if (fileDetails != null)
            {
                LogDebugForType(usageType, "Acquired permit for {UsageType}. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                LogDebugForType(usageType, "Acquired permit for {UsageType}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, currentUsage, GetUsageBreakdown());
            }
        }

        return new OperationPermit(this, usageType, acquiredLowPriorityGate, DateTime.UtcNow, fileDetails);
    }

    private void ReleasePermit(ConnectionUsageType usageType, bool releaseLowPriorityGate, DateTime acquiredAt, string? fileDetails)
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
                Log.Error("[GlobalPool] CRITICAL: Attempted to release permit for {UsageType} but usage counter is already 0!",
                    usageType);
            }
            currentUsage = _currentUsage[usageType];
        }

        // Release shared pool first, then low-priority gate
        _sharedPool.Release();
        if (releaseLowPriorityGate)
            _lowPriorityGate.Release();

        if (heldDuration.TotalMinutes > 5)
        {
            if (fileDetails != null)
            {
                Log.Warning("[GlobalPool] Released permit for {UsageType} after holding for {HeldMinutes:F1} minutes. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalMinutes, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                Log.Warning("[GlobalPool] Released permit for {UsageType} after holding for {HeldMinutes:F1} minutes. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
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

    private static SemaphorePriority GetPriorityForType(ConnectionUsageType type)
    {
        return type switch
        {
            ConnectionUsageType.Streaming => SemaphorePriority.High,
            ConnectionUsageType.BufferedStreaming => SemaphorePriority.High,
            _ => SemaphorePriority.Low
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
            Log.Debug("[GlobalPool] " + message, args);
            return;
        }

        var component = GetComponentForType(usageType);
        if (_configManager.IsDebugLogEnabled(component))
        {
            Log.Debug("[GlobalPool] " + message, args);
        }
    }

    private void LogInfoForType(ConnectionUsageType usageType, string message, params object[] args)
    {
        if (usageType == ConnectionUsageType.HealthCheck ||
            usageType == ConnectionUsageType.Repair ||
            usageType == ConnectionUsageType.Analysis ||
            usageType == ConnectionUsageType.QueueAnalysis ||
            usageType == ConnectionUsageType.QueueRarProcessing ||
            usageType == ConnectionUsageType.Streaming ||
            usageType == ConnectionUsageType.BufferedStreaming)
        {
            LogDebugForType(usageType, message, args);
            return;
        }

        Log.Information("[GlobalPool] " + message, args);
    }

    private static string GetComponentForType(ConnectionUsageType usageType)
    {
        return usageType switch
        {
            ConnectionUsageType.Queue => LogComponents.Queue,
            ConnectionUsageType.QueueRarProcessing => LogComponents.Queue,
            ConnectionUsageType.QueueAnalysis => LogComponents.Queue,
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
        _sharedPool.Dispose();
        _lowPriorityGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Represents a permit to perform an operation. Must be disposed to release the permit.
    /// </summary>
    public sealed class OperationPermit : IDisposable
    {
        private readonly GlobalOperationLimiter _limiter;
        private readonly ConnectionUsageType _usageType;
        private readonly bool _releaseLowPriorityGate;
        private readonly DateTime _acquiredAt;
        private readonly string? _fileDetails;
        private int _disposed;

        internal OperationPermit(GlobalOperationLimiter limiter, ConnectionUsageType usageType, bool releaseLowPriorityGate, DateTime acquiredAt, string? fileDetails)
        {
            _limiter = limiter;
            _usageType = usageType;
            _releaseLowPriorityGate = releaseLowPriorityGate;
            _acquiredAt = acquiredAt;
            _fileDetails = fileDetails;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _limiter.ReleasePermit(_usageType, _releaseLowPriorityGate, _acquiredAt, _fileDetails);
            }
        }
    }
}
