using System.Text.Json;
using System.Threading;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Direct coverage for JsonArchiveWriter's streaming fix: the old implementation built one
/// anonymous object holding every archived row, then called
/// JsonSerializer.Serialize(doc, new() { WriteIndented = true }) - producing one in-memory
/// string containing the entire archive before ever touching disk. For a real-world archive
/// (thousands of rows, some carrying non-trivial text) that string alone could be tens of
/// megabytes on top of everything else already resident in the pipeline.
/// </summary>
public class JsonArchiveWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"archive-writer-test-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Write_LargePayload_DoesNotBufferTheWholeOutputAsOneString()
    {
        // A payload big enough that a JsonSerializer.Serialize(doc)-shaped implementation would
        // need to hold an output string at least this large in memory before it could write a
        // single byte to disk (System.Text.Json's string-returning Serialize overload builds the
        // complete result before returning it).
        var archive = BuildArchiveWithApproximateSize(targetBytes: 20_000_000); // ~20MB

        var peakGrowth = MeasurePeakHeapGrowth(() => JsonArchiveWriter.Write(_path, archive));

        var fileInfo = new FileInfo(_path);
        Assert.True(fileInfo.Length > 15_000_000,
            $"expected the written archive to be at least 15MB to make this a meaningful test, was {fileInfo.Length:N0} bytes");

        // If Write() built one big string (or one big object graph) of the output before writing
        // it, peak growth would scale with the file size (20MB+). A true streaming writer's own
        // overhead (Utf8JsonWriter's internal buffer plus JSON-encoding one row at a time) stays
        // a small, roughly constant fraction of that regardless of how large the archive is.
        Assert.True(peakGrowth < fileInfo.Length / 4,
            $"JsonArchiveWriter.Write's peak heap growth was {peakGrowth:N0} bytes while writing a " +
            $"{fileInfo.Length:N0}-byte archive - expected well under a quarter of the output size, " +
            "which would indicate the whole output is being buffered as one string/object graph " +
            "rather than streamed.");
    }

    [Fact]
    public void Write_ProducesParseableJsonContainingEveryRow()
    {
        // The streaming rewrite must not change the archive's semantic content/shape - only how
        // it gets to disk. Round-trips a small archive through the real writer and checks every
        // section is present and correctly shaped, the same content-level guarantee the old
        // anonymous-object-based Write() gave.
        var obfuscated = new ArchivedObfuscatedFile(Guid.NewGuid(), "reason", "a2V5", "{\"seg\":1}");
        var fallback = new ArchivedNzbFileFallback(Guid.NewGuid(), ["seg-1", "seg-2"], [[], ["fb-1"]]);
        var archive = new ArchivePayload(
            LocalLinks: [], AnalysisHistoryItems: [], BandwidthSamples: [], MissingArticleEvents: [],
            MissingArticleSummaries: [], NzbProviderStats: [], ProviderBenchmarkResults: [],
            HistoryItemDroppedFields: [], HealthCheckDroppedOperations: [],
            SkippedConfigItems: [("usenet.host", "news.example.com")],
            SkippedObfuscatedFiles: [obfuscated], DavNzbFileFallbackIds: [fallback]);

        JsonArchiveWriter.Write(_path, archive);

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("GeneratedAtUtc", out _));
        Assert.True(root.TryGetProperty("Note", out _));

        var skippedObfuscated = root.GetProperty("SkippedObfuscatedFiles");
        Assert.Equal(1, skippedObfuscated.GetArrayLength());
        Assert.Equal("reason", skippedObfuscated[0].GetProperty("Reason").GetString());
        Assert.Equal("a2V5", skippedObfuscated[0].GetProperty("ObfuscationKeyBase64").GetString());

        var fallbackIds = root.GetProperty("DavNzbFileFallbackIds");
        Assert.Equal(1, fallbackIds.GetArrayLength());
        Assert.Equal("seg-1", fallbackIds[0].GetProperty("SegmentIds")[0].GetString());
        Assert.Equal("fb-1", fallbackIds[0].GetProperty("SegmentFallbackIds")[1][0].GetString());

        var skippedConfig = root.GetProperty("SkippedConfigItems");
        Assert.Equal(1, skippedConfig.GetArrayLength());
        Assert.Equal("usenet.host", skippedConfig[0].GetProperty("ConfigName").GetString());
    }

    private static ArchivePayload BuildArchiveWithApproximateSize(int targetBytes)
    {
        // ~5KB of source metadata text per skipped-obfuscated-file entry.
        const int perRowBytes = 5_000;
        var rowCount = targetBytes / perRowBytes;
        var bigText = new string('a', perRowBytes);

        var rows = Enumerable.Range(0, rowCount)
            .Select(i => new ArchivedObfuscatedFile(Guid.NewGuid(), "obfuscation key", null, bigText))
            .ToList();

        return new ArchivePayload(
            LocalLinks: [], AnalysisHistoryItems: [], BandwidthSamples: [], MissingArticleEvents: [],
            MissingArticleSummaries: [], NzbProviderStats: [], ProviderBenchmarkResults: [],
            HistoryItemDroppedFields: [], HealthCheckDroppedOperations: [], SkippedConfigItems: [],
            SkippedObfuscatedFiles: rows, DavNzbFileFallbackIds: []);
    }

    /// <summary>
    /// Same peak-sampling technique as StreamingMigratorMemoryTests.MeasurePeakHeapGrowth: polls
    /// GC.GetTotalMemory(forceFullCollection: false) on a background thread while
    /// <paramref name="work"/> runs, tracking the highest sample observed, and returns that peak
    /// minus a pre-run baseline. A single before/after diff would not work here: the heap size
    /// immediately after Write() returns reflects only what's still reachable once its locals are
    /// out of scope, not what was resident while it ran.
    /// </summary>
    private static long MeasurePeakHeapGrowth(Action work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var peak = baseline;
        var stop = new ManualResetEventSlim(false);
        var poller = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                var current = GC.GetTotalMemory(forceFullCollection: false);
                long observedPeak;
                do
                {
                    observedPeak = Volatile.Read(ref peak);
                    if (current <= observedPeak) break;
                } while (Interlocked.CompareExchange(ref peak, current, observedPeak) != observedPeak);
                Thread.Sleep(1);
            }
        })
        { IsBackground = true };
        poller.Start();
        try
        {
            work();
        }
        finally
        {
            stop.Set();
            poller.Join();
        }

        return Math.Max(0, peak - baseline);
    }
}
