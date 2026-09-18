using System.Text.Json;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Writes the sidecar JSON archive for data that has no destination in infinidysk. Streams
/// directly to a FileStream via Utf8JsonWriter, one row at a time - never builds the whole
/// archive as one in-memory string. Fixes an OOM: the old implementation built an anonymous
/// object holding every archived row, then called JsonSerializer.Serialize(doc) with
/// WriteIndented=true, materializing the entire archive (megabytes, for real-world databases
/// with thousands of AnalysisHistoryItems/BandwidthSamples rows) as one giant string in addition
/// to the source lists and target lists already held in memory by the rest of the pipeline.
///
/// Cosmetic-only change from before: output is now compact (no WriteIndented), since
/// Utf8JsonWriter still buffers internally regardless of indentation - the memory fix comes from
/// writing item-by-item instead of building one object graph, not from turning indentation off.
/// Compact was chosen anyway since nothing reads this file by hand at multi-thousand-row scale.
/// No existing test asserts exact archive formatting (checked before making this change).
/// </summary>
public static class JsonArchiveWriter
{
    public static void Write(string path, ArchivePayload archive)
    {
        using var session = new StreamingSession(path);
        session.WriteHeader();
        session.WriteArray("LocalLinks", archive.LocalLinks);
        session.WriteArray("AnalysisHistoryItems", archive.AnalysisHistoryItems);
        session.WriteArray("BandwidthSamples", archive.BandwidthSamples);
        session.WriteArray("MissingArticleEvents", archive.MissingArticleEvents);
        session.WriteArray("MissingArticleSummaries", archive.MissingArticleSummaries);
        session.WriteArray("NzbProviderStats", archive.NzbProviderStats);
        session.WriteArray("ProviderBenchmarkResults", archive.ProviderBenchmarkResults);
        session.WriteArray("HistoryItemDroppedFields", archive.HistoryItemDroppedFields);
        session.WriteArray("HealthCheckDroppedOperations", archive.HealthCheckDroppedOperations);
        session.BeginArray("SkippedConfigItems");
        foreach (var c in archive.SkippedConfigItems)
            session.WriteItem(new { c.ConfigName, c.ConfigValue });
        session.EndArray();
        session.WriteArray("SkippedObfuscatedFiles", archive.SkippedObfuscatedFiles);
        session.WriteArray("DavNzbFileFallbackIds", archive.DavNzbFileFallbackIds);
        session.Finish();
    }

    /// <summary>
    /// Drives the same streamed-array shape as <see cref="Write"/>, but lets a caller push rows
    /// one at a time as it streams them from the source database, instead of handing over a
    /// fully materialized <see cref="ArchivePayload"/>. Used by StreamingMigrator for the big
    /// tables (AnalysisHistoryItems, BandwidthSamples, etc.) so the archive is written
    /// incrementally, in lockstep with the source read, and neither side ever holds the whole
    /// table in memory at once.
    /// </summary>
    public sealed class StreamingSession : IDisposable
    {
        private readonly FileStream _stream;
        private readonly Utf8JsonWriter _writer;
        private bool _headerWritten;
        private bool _finished;

        public StreamingSession(string path)
        {
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new Utf8JsonWriter(_stream); // default options: compact, streaming - see class doc comment
        }

        public void WriteHeader()
        {
            _writer.WriteStartObject();
            _writer.WriteString("GeneratedAtUtc", DateTime.UtcNow);
            _writer.WriteString("Note",
                "Data with no infinidysk equivalent, archived by nzbdav2's migrate-to-infinidysk tool. " +
                "See MIGRATING_TO_INFINIDYSK.md 'What is NOT preserved'.");
            _headerWritten = true;
        }

        public void BeginArray(string propertyName)
        {
            _writer.WritePropertyName(propertyName);
            _writer.WriteStartArray();
        }

        public void WriteItem<T>(T item) => JsonSerializer.Serialize(_writer, item);

        public void EndArray() => _writer.WriteEndArray();

        public void WriteArray<T>(string propertyName, IEnumerable<T> items)
        {
            BeginArray(propertyName);
            foreach (var item in items)
                WriteItem(item);
            EndArray();
        }

        public void Finish()
        {
            if (!_headerWritten)
                throw new InvalidOperationException("WriteHeader must be called before Finish.");
            _writer.WriteEndObject();
            _writer.Flush();
            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                // Best-effort: an exception mid-write leaves an incomplete JSON file at this
                // path. Callers (StreamingMigrator/MigrationApplier) always write to a temp path
                // and only rename it into place after a clean Finish(), so an incomplete file
                // here is never the one that gets published.
                try { _writer.Flush(); } catch { /* already broken, nothing more to do */ }
            }
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
