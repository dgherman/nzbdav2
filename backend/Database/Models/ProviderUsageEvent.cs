namespace NzbWebDAV.Database.Models;

public class ProviderUsageEvent
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string ProviderHost { get; init; } = "";
    public string ProviderType { get; init; } = "";
    public string? OperationType { get; init; }
    public long? BytesTransferred { get; init; }
}
