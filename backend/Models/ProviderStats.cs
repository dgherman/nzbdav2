namespace NzbWebDAV.Models;

public class ProviderStats
{
    public required string ProviderHost { get; init; }
    public required string ProviderType { get; init; }
    public long TotalOperations { get; init; }
    public Dictionary<string, long> OperationCounts { get; init; } = new();
    public double PercentageOfTotal { get; init; }
}

public class ProviderStatsResponse
{
    public required List<ProviderStats> Providers { get; init; }
    public long TotalOperations { get; init; }
    public DateTimeOffset CalculatedAt { get; init; }
    public TimeSpan TimeWindow { get; init; }
    public int TimeWindowHours { get; init; }
}
