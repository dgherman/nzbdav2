namespace NzbWebDAV.Api.Controllers.ProviderBenchmark;

public class ProviderBenchmarkRequest
{
    /// <summary>
    /// List of provider indices to test. If null or empty, tests all non-disabled providers.
    /// </summary>
    public List<int>? ProviderIndices { get; set; }

    /// <summary>
    /// Whether to include a load-balanced test at the end.
    /// </summary>
    public bool IncludeLoadBalanced { get; set; } = true;

    /// <summary>
    /// Optional: Specify a file ID to use for the benchmark. If null, a random large file is selected.
    /// This allows re-running benchmarks with the same file for consistent comparisons.
    /// </summary>
    public Guid? FileId { get; set; }
}

public class ProviderBenchmarkResponse : BaseApiResponse
{
    public Guid RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? TestFileName { get; set; }
    public long TestFileSize { get; set; }
    public int TestSizeMb { get; set; }
    public List<ProviderBenchmarkResultDto> Results { get; set; } = new();

    /// <summary>
    /// True when the benchmark has completed (either success or failure).
    /// Used by WebSocket progress updates to signal completion.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Total number of providers to test (for progress calculation).
    /// </summary>
    public int TotalProviders { get; set; }

    /// <summary>
    /// The file ID used for this benchmark. Can be passed back in subsequent requests to reuse the same file.
    /// </summary>
    public Guid? TestFileId { get; set; }
}

public class ProviderBenchmarkResultDto
{
    public int ProviderIndex { get; set; }
    public string ProviderHost { get; set; } = "";
    public string ProviderType { get; set; } = "";
    public bool IsLoadBalanced { get; set; }
    public long BytesDownloaded { get; set; }
    public double ElapsedSeconds { get; set; }
    public double SpeedMbps { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ProviderBenchmarkHistoryResponse : BaseApiResponse
{
    public List<ProviderBenchmarkRunSummary> Runs { get; set; } = new();
}

public class ProviderBenchmarkRunSummary
{
    public Guid RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? TestFileName { get; set; }
    public long TestFileSize { get; set; }
    public int TestSizeMb { get; set; }
    public List<ProviderBenchmarkResultDto> Results { get; set; } = new();
}

public class ProviderListResponse : BaseApiResponse
{
    public List<ProviderInfoDto> Providers { get; set; } = new();
}

public class ProviderInfoDto
{
    public int Index { get; set; }
    public string Host { get; set; } = "";
    public string Type { get; set; } = "";
    public int MaxConnections { get; set; }
    public bool IsDisabled { get; set; }
}
