namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionUsageDetails
{
    public string Text { get; init; } = "";
    public string? JobName { get; init; }
    public string? AffinityKey { get; init; }
    public Guid? DavItemId { get; set; }
    public DateTimeOffset? FileDate { get; set; }
    public bool IsBackup { get; set; }
    public bool IsSecondary { get; set; }
    public bool IsImported { get; set; }
    public int? BufferedCount { get; set; }
    public int? BufferWindowStart { get; set; }
    public int? BufferWindowEnd { get; set; }
    public int? TotalSegments { get; set; }
    public long? CurrentBytePosition { get; set; }
    public long? FileSize { get; set; }
    public long? BaseByteOffset { get; set; }  // Starting byte offset for partial streams

    /// <summary>
    /// Forces all operations to use a specific provider index, bypassing affinity and load balancing.
    /// Used for testing individual provider performance. -1 or null means no forced provider.
    /// </summary>
    public int? ForcedProviderIndex { get; init; }

    /// <summary>
    /// Provider indices to exclude from selection (e.g., providers that recently failed for this segment).
    /// Used by straggler retry logic to try a different provider on each retry attempt.
    /// </summary>
    public HashSet<int>? ExcludedProviderIndices { get; set; }

    /// <summary>
    /// The provider index currently being used for this operation.
    /// Set by MultiProviderNntpClient when a provider is selected, read by straggler detection
    /// to know which provider to exclude on retry.
    /// </summary>
    public int? CurrentProviderIndex { get; set; }

    /// <summary>
    /// When true, disables graceful degradation (zero-fill on permanent failure).
    /// Instead, throws PermanentSegmentFailureException. Used for benchmarks.
    /// </summary>
    public bool DisableGracefulDegradation { get; init; }

    public ConnectionUsageDetails Clone()
    {
        return new ConnectionUsageDetails
        {
            Text = Text,
            JobName = JobName,
            AffinityKey = AffinityKey,
            DavItemId = DavItemId,
            FileDate = FileDate,
            IsBackup = IsBackup,
            IsSecondary = IsSecondary,
            IsImported = IsImported,
            BufferedCount = BufferedCount,
            BufferWindowStart = BufferWindowStart,
            BufferWindowEnd = BufferWindowEnd,
            TotalSegments = TotalSegments,
            CurrentBytePosition = CurrentBytePosition,
            FileSize = FileSize,
            BaseByteOffset = BaseByteOffset,
            ForcedProviderIndex = ForcedProviderIndex,
            ExcludedProviderIndices = ExcludedProviderIndices != null ? new HashSet<int>(ExcludedProviderIndices) : null,
            CurrentProviderIndex = CurrentProviderIndex,
            DisableGracefulDegradation = DisableGracefulDegradation
        };
    }

    public override string ToString()
    {
        if (FileDate.HasValue)
        {
            var age = DateTimeOffset.UtcNow - FileDate.Value;
            return $"{Text} ({age.Days}d ago)";
        }
        return Text;
    }
}

/// <summary>
/// Tracks what a connection is being used for (queue processing, streaming, health checks/repair)
/// </summary>
public readonly struct ConnectionUsageContext
{
    public ConnectionUsageType UsageType { get; }

    private readonly ConnectionUsageDetails? _detailsObj;
    private readonly string? _detailsStr;

    public string? Details => _detailsObj?.ToString() ?? _detailsStr;
    public string? JobName => _detailsObj?.JobName ?? Details;
    public string? AffinityKey => _detailsObj?.AffinityKey ?? JobName;
    public bool IsBackup => _detailsObj?.IsBackup ?? false;
    public bool IsSecondary => _detailsObj?.IsSecondary ?? false;
    public bool IsImported => _detailsObj?.IsImported ?? false;

    public ConnectionUsageDetails? DetailsObject => _detailsObj;

    /// <summary>
    /// Provider indices to completely exclude from selection (per-operation).
    /// These providers will not be used at all for this operation.
    /// </summary>
    public HashSet<int>? ExcludedProviderIndices { get; init; }

    /// <summary>
    /// Provider indices to deprioritize (use as last resort).
    /// These providers are in cooldown due to recent failures/slowness.
    /// They will still be used if no other providers are available.
    /// </summary>
    public HashSet<int>? DeprioritizedProviderIndices { get; init; }

    public ConnectionUsageContext(ConnectionUsageType usageType, string? details = null)
    {
        UsageType = usageType;
        _detailsStr = details;
        _detailsObj = null;
        ExcludedProviderIndices = null;
        DeprioritizedProviderIndices = null;
    }

    public ConnectionUsageContext(ConnectionUsageType usageType, ConnectionUsageDetails details)
    {
        UsageType = usageType;
        _detailsObj = details;
        _detailsStr = null;
        ExcludedProviderIndices = null;
        DeprioritizedProviderIndices = null;
    }

    /// <summary>
    /// Creates a copy of this context with the specified excluded providers.
    /// Used for per-segment provider exclusion after failures.
    /// </summary>
    public ConnectionUsageContext WithExcludedProviders(HashSet<int>? excluded)
    {
        return CreateAdjustedContext(
            excluded,
            DeprioritizedProviderIndices);
    }

    private ConnectionUsageContext CreateAdjustedContext(HashSet<int>? excluded, HashSet<int>? deprioritized)
    {
        var adjusted = _detailsObj != null
            ? new ConnectionUsageContext(UsageType, _detailsObj.Clone())
            : new ConnectionUsageContext(UsageType, _detailsStr);

        return adjusted with
        {
            ExcludedProviderIndices = excluded != null ? new HashSet<int>(excluded) : null,
            DeprioritizedProviderIndices = deprioritized != null ? new HashSet<int>(deprioritized) : null
        };
    }

    /// <summary>
    /// Creates a copy of this context with both excluded and deprioritized providers.
    /// Used for per-job context with cooldown information.
    /// </summary>
    public ConnectionUsageContext WithProviderAdjustments(HashSet<int>? excluded, HashSet<int>? deprioritized)
    {
        return CreateAdjustedContext(excluded, deprioritized);
    }

    public override string ToString()
    {
        return Details != null ? $"{UsageType}:{Details}" : UsageType.ToString();
    }
}

public enum ConnectionUsageType
{
    Unknown = 0,
    Queue = 1,
    Streaming = 2,
    HealthCheck = 3,
    Repair = 4,
    BufferedStreaming = 5,
    Analysis = 6,
    QueueAnalysis = 7,
    QueueRarProcessing = 8
}
