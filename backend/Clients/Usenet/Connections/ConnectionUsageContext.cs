namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionUsageDetails
{
    public string Text { get; init; } = "";
    public DateTimeOffset? FileDate { get; set; }

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
    
    public ConnectionUsageDetails? DetailsObject => _detailsObj;

    public ConnectionUsageContext(ConnectionUsageType usageType, string? details = null)
    {
        UsageType = usageType;
        _detailsStr = details;
        _detailsObj = null;
    }
    
    public ConnectionUsageContext(ConnectionUsageType usageType, ConnectionUsageDetails details)
    {
        UsageType = usageType;
        _detailsObj = details;
        _detailsStr = null;
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
