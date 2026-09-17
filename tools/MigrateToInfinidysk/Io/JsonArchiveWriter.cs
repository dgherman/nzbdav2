using System.Text.Json;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>Writes the sidecar JSON archive for data that has no destination in infinidysk.</summary>
public static class JsonArchiveWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(string path, ArchivePayload archive)
    {
        var doc = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Note = "Data with no infinidysk equivalent, archived by nzbdav2's migrate-to-infinidysk tool. " +
                   "See MIGRATING_TO_INFINIDYSK.md 'What is NOT preserved'.",
            archive.LocalLinks,
            archive.AnalysisHistoryItems,
            archive.BandwidthSamples,
            archive.MissingArticleEvents,
            archive.MissingArticleSummaries,
            archive.NzbProviderStats,
            archive.ProviderBenchmarkResults,
            archive.HistoryItemDroppedFields,
            archive.HealthCheckDroppedOperations,
            SkippedConfigItems = archive.SkippedConfigItems.Select(c => new { c.ConfigName, c.ConfigValue }),
            archive.SkippedObfuscatedFiles,
            archive.DavNzbFileFallbackIds,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(doc, Options));
    }
}
