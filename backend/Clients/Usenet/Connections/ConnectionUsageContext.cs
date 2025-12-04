namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks what a connection is being used for (queue processing, streaming, health checks/repair)
/// </summary>
public readonly struct ConnectionUsageContext
{
    public ConnectionUsageType UsageType { get; }
    public string? Details { get; }

    public ConnectionUsageContext(ConnectionUsageType usageType, string? details = null)
    {
        UsageType = usageType;
        Details = details;
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
    BufferedStreaming = 5
}
