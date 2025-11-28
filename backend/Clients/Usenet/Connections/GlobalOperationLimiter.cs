using NzbWebDAV.Extensions;

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

    public GlobalOperationLimiter(
        int maxQueueConnections,
        int maxHealthCheckConnections,
        int totalConnections)
    {
        var maxStreamingConnections = Math.Max(1, totalConnections - maxQueueConnections - maxHealthCheckConnections);

        _guaranteedLimits = new Dictionary<ConnectionUsageType, int>
        {
            { ConnectionUsageType.Queue, maxQueueConnections },
            { ConnectionUsageType.HealthCheck, maxHealthCheckConnections },
            { ConnectionUsageType.Streaming, maxStreamingConnections },
            { ConnectionUsageType.BufferedStreaming, maxStreamingConnections },
            { ConnectionUsageType.Repair, maxHealthCheckConnections }, // Share with HealthCheck
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

        Serilog.Log.Information($"[GlobalOperationLimiter] Initialized: Queue={maxQueueConnections}, HealthCheck={maxHealthCheckConnections}, Streaming={maxStreamingConnections}, Total={totalConnections}");
    }

    /// <summary>
    /// Acquires a permit for the given operation type. Must be released via ReleasePermit.
    /// </summary>
    public async Task<OperationPermit> AcquirePermitAsync(ConnectionUsageType usageType, CancellationToken cancellationToken = default)
    {
        var semaphore = GetSemaphoreForType(usageType);
        var guaranteedLimit = _guaranteedLimits[usageType];

        // Wait for the operation-specific semaphore
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Track usage
        int currentUsage;
        lock (_lock)
        {
            _currentUsage[usageType]++;
            currentUsage = _currentUsage[usageType];
        }

        Serilog.Log.Debug($"[GlobalOperationLimiter] Acquired permit for {usageType}: Current={currentUsage}, Guaranteed={guaranteedLimit}, Usage={GetUsageBreakdown()}");

        return new OperationPermit(this, usageType, semaphore);
    }

    private void ReleasePermit(ConnectionUsageType usageType, SemaphoreSlim semaphore)
    {
        lock (_lock)
        {
            if (_currentUsage.ContainsKey(usageType) && _currentUsage[usageType] > 0)
            {
                _currentUsage[usageType]--;
            }
        }

        semaphore.Release();
        Serilog.Log.Debug($"[GlobalOperationLimiter] Released permit for {usageType}: Usage={GetUsageBreakdown()}");
    }

    private SemaphoreSlim GetSemaphoreForType(ConnectionUsageType type)
    {
        return type switch
        {
            ConnectionUsageType.Queue => _queueSemaphore,
            ConnectionUsageType.HealthCheck => _healthCheckSemaphore,
            ConnectionUsageType.Repair => _healthCheckSemaphore, // Share with HealthCheck
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

    public void Dispose()
    {
        _queueSemaphore.Dispose();
        _healthCheckSemaphore.Dispose();
        _streamingSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Represents a permit to perform an operation. Must be disposed to release the permit.
    /// </summary>
    public readonly struct OperationPermit : IDisposable
    {
        private readonly GlobalOperationLimiter _limiter;
        private readonly ConnectionUsageType _usageType;
        private readonly SemaphoreSlim _semaphore;

        internal OperationPermit(GlobalOperationLimiter limiter, ConnectionUsageType usageType, SemaphoreSlim semaphore)
        {
            _limiter = limiter;
            _usageType = usageType;
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            _limiter.ReleasePermit(_usageType, _semaphore);
        }
    }
}
