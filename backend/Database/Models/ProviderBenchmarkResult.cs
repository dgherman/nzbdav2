using System;

namespace NzbWebDAV.Database.Models;

public class ProviderBenchmarkResult
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; } // Groups all results from same benchmark run
    public DateTimeOffset CreatedAt { get; set; }
    public required string TestFileName { get; set; }
    public long TestFileSize { get; set; }
    public int TestSizeMb { get; set; }
    public int ProviderIndex { get; set; }
    public required string ProviderHost { get; set; }
    public required string ProviderType { get; set; }
    public bool IsLoadBalanced { get; set; }
    public long BytesDownloaded { get; set; }
    public double ElapsedSeconds { get; set; }
    public double SpeedMbps { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
