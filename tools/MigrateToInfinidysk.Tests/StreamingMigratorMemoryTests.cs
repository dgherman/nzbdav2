using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Regression coverage for the round-12 OOM fix: --apply against a real-world-sized source
/// database (thousands of AnalysisHistoryItems/BandwidthSamples rows, hundreds of
/// QueueNzbContents rows carrying large NZB XML text, thousands of skipped DavMultipartFiles
/// rows) could crash even under an 8GB container memory limit, because the whole pipeline
/// (SqliteSourceReader -> Migrator -> JsonArchiveWriter/SqliteTargetWriter) materialized the
/// full source read, the full mapped target rows, AND the full archive payload simultaneously.
///
/// Both the pre-fix pipeline (SqliteSourceReader.Read + Migrator.Run + MigrationApplier.Apply -
/// still present in this codebase, since Migrator.Run is kept for the fixture-based unit tests
/// elsewhere in this project) and the post-fix pipeline (StreamingMigrator.Run, what Program.cs
/// actually calls now) are exercised here against the identical synthetic fixture, so this test
/// demonstrably fails against the pre-fix pipeline and passes against the post-fix one without
/// needing to hand-revert any code.
///
/// Peak managed heap size DURING the call (not cumulative bytes allocated, and not the heap size
/// after the call returns) is the signal that actually distinguishes "hold every row of a big
/// table at once" from "stream one row at a time": a list holding 300 same-size strings
/// simultaneously and a loop touching those same 300 strings one at a time and discarding each
/// allocate roughly the same TOTAL bytes over the run (so GC.GetTotalAllocatedBytes can't tell
/// them apart), but they have very different PEAK heap sizes, because only the "hold them all"
/// case keeps every string reachable at once. This is measured with a background thread polling
/// GC.GetTotalMemory(forceFullCollection: false) at a short interval while the pipeline runs on
/// the calling thread, tracking the maximum sample observed - a peak-sampling technique, not a
/// before/after diff, since a before/after diff of the heap size after the call returns would
/// show close to zero for both pipelines once their locals go out of scope.
/// </summary>
public class StreamingMigratorMemoryTests : IDisposable
{
    // Sized to produce a clear, reliable order-of-magnitude gap between the two pipelines while
    // keeping the test itself fast - real-world reports were an order of magnitude larger still
    // (11512 AnalysisHistoryItems, 4134 BandwidthSamples, 675 QueueNzbContents with full NZB XML
    // text) and OOM'd even at 8GB; this fixture only needs to be big enough that "materialize
    // everything at once" and "stream it" are clearly, measurably different, not to reproduce
    // the exact real-world scale.
    private const int AnalysisHistoryItemCount = 6000;
    private const int BandwidthSampleCount = 3000;
    private const int QueueNzbContentsCount = 300;
    private const int QueueNzbContentsTextBytes = 100_000; // ~100KB of "NZB XML" per row
    private const int SkippedMultipartCount = 1200;
    private const int HistoryItemCount = 2000;

    // The streaming pipeline's PEAK heap growth over its own baseline must stay well under this.
    // The pre-fix pipeline, over this identical fixture, peaks far higher (see
    // Streaming_PeaksFarLowerThanPreFixPipeline_OverIdenticalFixture below) - this ceiling is
    // comfortably above the streaming pipeline's real peak (a handful of small lookup structures
    // plus one row at a time) and comfortably below what holding the whole fixture's big tables
    // at once requires (QueueNzbContentsCount * QueueNzbContentsTextBytes alone is ~30MB of text,
    // held twice over - source list and target list - in the pre-fix pipeline).
    private const long StreamingPeakCeilingBytes = 40_000_000; // 40MB

    private readonly string _sourceDbPath = Path.Combine(Path.GetTempPath(), $"nzbdav2-memfixture-{Guid.NewGuid()}.sqlite");
    private readonly string _targetDbPath = Path.Combine(Path.GetTempPath(), $"infinidysk-memfixture-{Guid.NewGuid()}.sqlite");
    private readonly string _archivePath = Path.Combine(Path.GetTempPath(), $"archive-memfixture-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_sourceDbPath);
        File.Delete(_targetDbPath);
        if (File.Exists(_archivePath)) File.Delete(_archivePath);
        var oldArchive = _archivePath + ".oldpath";
        if (File.Exists(oldArchive)) File.Delete(oldArchive);
    }

    [Fact]
    public void StreamingMigrator_Apply_StaysUnderPeakMemoryCeiling_OnRealisticallySizedFixture()
    {
        BuildSourceFixture();

        using var target = BuildTargetFixture();
        using var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();

        MigrationResult? result = null;
        var peakGrowth = MeasurePeakHeapGrowth(() =>
            result = StreamingMigrator.Run(sourceConn, target, _archivePath, new MigrationOptions(null)));

        Assert.True(result!.Success, string.Join("; ", result.Errors));
        Assert.True(peakGrowth < StreamingPeakCeilingBytes,
            $"StreamingMigrator.Run's peak heap growth was {peakGrowth:N0} bytes, expected under " +
            $"{StreamingPeakCeilingBytes:N0}. This is the exact regression round-12 fixes - a " +
            "streaming pipeline must not hold the big tables' rows all at once.");

        // Sanity: the run actually processed the fixture's full scale, not an empty/short-circuited
        // pipeline that would trivially stay under any ceiling.
        Assert.Equal(AnalysisHistoryItemCount, result.Counts["AnalysisHistoryItems"].Archived);
        Assert.Equal(BandwidthSampleCount, result.Counts["BandwidthSamples"].Archived);
        Assert.Equal(QueueNzbContentsCount, result.Counts["QueueNzbContents"].Copied);
        Assert.Equal(SkippedMultipartCount, result.Counts["DavMultipartFiles"].Skipped);
        Assert.Equal(HistoryItemCount, result.Counts["HistoryItems"].Copied);
        Assert.True(File.Exists(_archivePath));
    }

    [Fact]
    public void Streaming_PeaksFarLowerThanPreFixPipeline_OverIdenticalFixture()
    {
        // Proves the fix actually fixes something: runs the OLD (pre-fix) pipeline - still
        // present in this codebase as SqliteSourceReader.Read + Migrator.Run +
        // MigrationApplier.Apply, kept because Migrator.Run itself is exercised directly by the
        // fixture-based unit tests elsewhere in this project - against the identical fixture used
        // above, and asserts its peak heap growth is dramatically higher than StreamingMigrator's.
        // If StreamingMigrator ever regresses back toward materializing everything, this test's
        // ratio assertion (not just the ceiling in the test above) would catch it even if someone
        // widened the ceiling constant.
        BuildSourceFixture();

        long oldPipelinePeak;
        using (var oldTarget = BuildTargetFixture())
        using (var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly"))
        {
            sourceConn.Open();
            oldPipelinePeak = MeasurePeakHeapGrowth(() =>
            {
                var snapshot = SqliteSourceReader.Read(sourceConn);
                var result = Migrator.Run(snapshot, new MigrationOptions(null));
                Assert.True(result.Success, string.Join("; ", result.Errors));
                var oldArchivePath = _archivePath + ".oldpath";
                MigrationApplier.Apply(oldTarget, result, oldArchivePath);
            });
        }

        long streamingPeak;
        using (var newTarget = BuildTargetFixture())
        using (var sourceConn = new SqliteConnection($"Data Source={_sourceDbPath};Mode=ReadOnly"))
        {
            sourceConn.Open();
            streamingPeak = MeasurePeakHeapGrowth(() =>
            {
                var result = StreamingMigrator.Run(sourceConn, newTarget, _archivePath, new MigrationOptions(null));
                Assert.True(result.Success, string.Join("; ", result.Errors));
            });
        }

        Assert.True(streamingPeak < StreamingPeakCeilingBytes,
            $"streaming pipeline peak heap growth was {streamingPeak:N0} bytes, expected under {StreamingPeakCeilingBytes:N0}");

        // The old pipeline must exceed the streaming ceiling on this exact fixture - this is what
        // "fails pre-fix, passes post-fix" means here: both pipelines run against byte-identical
        // input, and only the streaming one meets the bound.
        Assert.True(oldPipelinePeak > StreamingPeakCeilingBytes,
            $"pre-fix pipeline peak heap growth was only {oldPipelinePeak:N0} bytes (expected > {StreamingPeakCeilingBytes:N0}) - " +
            "the fixture may be too small to demonstrate the regression this test exists to catch.");

        // The gap should be large, not marginal - a real fix removes whole-table duplication, not
        // shaves a percentage off it.
        Assert.True(oldPipelinePeak > streamingPeak * 3,
            $"expected the pre-fix pipeline's peak ({oldPipelinePeak:N0} bytes) to be at least " +
            $"3x the streaming pipeline's peak ({streamingPeak:N0} bytes)");
    }

    /// <summary>
    /// Runs <paramref name="work"/> while a background thread polls
    /// GC.GetTotalMemory(forceFullCollection: false) at a short interval, tracking the highest
    /// sample observed. Returns that peak minus a pre-run baseline (also from a forced, full
    /// collection) - i.e. how much the heap grew above its resting size at some point during the
    /// call, which is what distinguishes "held everything at once" from "streamed it" (see class
    /// doc comment). Polling (not a single before/after diff) is required because the heap size
    /// immediately after <paramref name="work"/> returns reflects only what's still reachable
    /// once all its locals have gone out of scope - by then, even the "held everything at once"
    /// pipeline's lists are typically unreachable and would measure close to zero.
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
                Thread.Sleep(2);
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

    private void BuildSourceFixture()
    {
        using var conn = new SqliteConnection($"Data Source={_sourceDbPath}");
        conn.Open();
        Execute(conn, """
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO __EFMigrationsHistory VALUES ('20251113081523_Populate-Usenet-Providers-Config', '10.0.4');

            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, Path TEXT NOT NULL,
                ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER, MediaInfo TEXT,
                IsCorrupted INTEGER NOT NULL DEFAULT 0, CorruptionReason TEXT, HistoryItemId TEXT);

            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL, SegmentFallbacks TEXT);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE DavRarFiles (Id TEXT PRIMARY KEY, RarParts TEXT NOT NULL);

            CREATE TABLE QueueItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, FileName TEXT NOT NULL, JobName TEXT NOT NULL,
                NzbFileSize INTEGER NOT NULL, TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL,
                Priority INTEGER NOT NULL, PostProcessing INTEGER NOT NULL, PauseUntil TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);

            CREATE TABLE HistoryItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, CompletedAt TEXT NOT NULL, FileName TEXT NOT NULL,
                JobName TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT,
                DownloadDirId TEXT, IsHidden INTEGER NOT NULL DEFAULT 0, HiddenAt TEXT, NzbContents TEXT,
                FailureReason TEXT, IsImported INTEGER NOT NULL DEFAULT 0, IsArchived INTEGER NOT NULL DEFAULT 0,
                ArchivedAt TEXT);

            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            INSERT INTO Accounts VALUES (1, 'admin', 'hash', 'salt');
            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT, Operation TEXT NOT NULL DEFAULT 'UNKNOWN');
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY (DateStartInclusive, DateEndExclusive, Result, RepairStatus));

            CREATE TABLE AnalysisHistoryItems (Id TEXT PRIMARY KEY, DavItemId TEXT NOT NULL, FileName TEXT NOT NULL, JobName TEXT, CreatedAt INTEGER NOT NULL, Result TEXT NOT NULL, Details TEXT, DurationMs INTEGER NOT NULL);
            CREATE TABLE BandwidthSamples (Id INTEGER PRIMARY KEY, ProviderIndex INTEGER NOT NULL, Bytes INTEGER NOT NULL, Timestamp INTEGER NOT NULL);
            """);

        InsertBulk(conn, AnalysisHistoryItemCount,
            "INSERT INTO AnalysisHistoryItems (Id, DavItemId, FileName, JobName, CreatedAt, Result, Details, DurationMs) VALUES ($id, $davItemId, $fileName, $jobName, $createdAt, $result, $details, $durationMs)",
            (cmd, i) =>
            {
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("$davItemId", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("$fileName", $"movie-{i}.mkv");
                cmd.Parameters.AddWithValue("$jobName", $"job-{i}");
                cmd.Parameters.AddWithValue("$createdAt", 1700000000L + i);
                cmd.Parameters.AddWithValue("$result", "Success");
                cmd.Parameters.AddWithValue("$details", new string('x', 500));
                cmd.Parameters.AddWithValue("$durationMs", 1234L);
            });

        InsertBulk(conn, BandwidthSampleCount,
            "INSERT INTO BandwidthSamples (ProviderIndex, Bytes, Timestamp) VALUES ($p, $b, $t)",
            (cmd, i) =>
            {
                cmd.Parameters.AddWithValue("$p", i % 4);
                cmd.Parameters.AddWithValue("$b", 123456789L);
                cmd.Parameters.AddWithValue("$t", 1700000000L + i);
            });

        var queueNzbText = new string('N', QueueNzbContentsTextBytes);
        InsertBulk(conn, QueueNzbContentsCount,
            "INSERT INTO QueueItems (Id, CreatedAt, FileName, JobName, NzbFileSize, TotalSegmentBytes, Category, Priority, PostProcessing) VALUES ($id, $createdAt, $fileName, $jobName, 1000, 1000, 'movies', 0, 0)",
            (cmd, i) =>
            {
                var id = Guid.NewGuid();
                _queueIds.Add(id);
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.Parameters.AddWithValue("$createdAt", "2026-01-01 00:00:00");
                cmd.Parameters.AddWithValue("$fileName", $"queue-{i}.mkv");
                cmd.Parameters.AddWithValue("$jobName", $"queue-job-{i}");
            });
        InsertBulk(conn, QueueNzbContentsCount,
            "INSERT INTO QueueNzbContents (Id, NzbContents) VALUES ($id, $nzb)",
            (cmd, i) =>
            {
                cmd.Parameters.AddWithValue("$id", _queueIds[i].ToString());
                cmd.Parameters.AddWithValue("$nzb", queueNzbText);
            });

        var multipartMetaTemplate = JsonSerializer.Serialize(new
        {
            AesParams = (object?)null,
            ObfuscationKey = (byte[]?)null, // null key + RAR-sourced => always skipped, see ObfuscationDetector
            FileParts = new[]
            {
                new
                {
                    SegmentIds = Enumerable.Range(0, 20).Select(n => $"seg-{n}@example").ToArray(),
                    SegmentIdByteRange = new { StartInclusive = 0, EndExclusive = 1_000_000 },
                    FilePartByteRange = new { StartInclusive = 0, EndExclusive = 1_000_000 },
                    SegmentFallbacks = (object?)null,
                    SegmentSizes = (object?)null,
                },
            },
        });
        InsertBulk(conn, SkippedMultipartCount,
            "INSERT INTO DavItems (Id, IdPrefix, CreatedAt, ParentId, Name, Type, Path) VALUES ($id, $prefix, '2026-01-01 00:00:00', NULL, $name, 4, $path)",
            (cmd, i) =>
            {
                var id = Guid.NewGuid();
                _multipartIds.Add(id);
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.Parameters.AddWithValue("$prefix", id.ToString()[..5]);
                cmd.Parameters.AddWithValue("$name", $"rar-{i}.mkv");
                cmd.Parameters.AddWithValue("$path", $"/content/rar-{i}.mkv");
            });
        InsertBulk(conn, SkippedMultipartCount,
            "INSERT INTO DavMultipartFiles (Id, Metadata) VALUES ($id, $meta)",
            (cmd, i) =>
            {
                cmd.Parameters.AddWithValue("$id", _multipartIds[i].ToString());
                cmd.Parameters.AddWithValue("$meta", multipartMetaTemplate);
            });

        InsertBulk(conn, HistoryItemCount,
            "INSERT INTO HistoryItems (Id, CreatedAt, CompletedAt, FileName, JobName, Category, DownloadStatus, TotalSegmentBytes, DownloadTimeSeconds) VALUES ($id, '2026-01-01 00:00:00', '2026-01-01 00:05:00', $fileName, $jobName, 'movies', 1, 5000000, 300)",
            (cmd, i) =>
            {
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("$fileName", $"history-{i}.mkv");
                cmd.Parameters.AddWithValue("$jobName", $"history-job-{i}");
            });
    }

    private readonly List<Guid> _queueIds = [];
    private readonly List<Guid> _multipartIds = [];

    private static void InsertBulk(SqliteConnection conn, int count, string sql, Action<SqliteCommand, int> bind)
    {
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            bind(cmd, i);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private SqliteConnection BuildTargetFixture()
    {
        var conn = new SqliteConnection($"Data Source={_targetDbPath};Mode=ReadWriteCreate");
        conn.Open();
        Execute(conn, """
            DROP TABLE IF EXISTS DavItems; DROP TABLE IF EXISTS DavNzbFiles; DROP TABLE IF EXISTS DavMultipartFiles;
            DROP TABLE IF EXISTS DavRarFiles; DROP TABLE IF EXISTS QueueItems; DROP TABLE IF EXISTS QueueNzbContents;
            DROP TABLE IF EXISTS HistoryItems; DROP TABLE IF EXISTS ConfigItems; DROP TABLE IF EXISTS Accounts;
            DROP TABLE IF EXISTS HealthCheckResults; DROP TABLE IF EXISTS HealthCheckStats;

            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL DEFAULT 0,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL DEFAULT 0, FileBlobId TEXT, HistoryItemId TEXT, NzbBlobId TEXT,
                ArrDownloadId TEXT, GeneratedStrmOutputRoot TEXT, GeneratedStrmPath TEXT, GeneratedStrmTarget TEXT,
                GeneratedSymlinkOutputRoot TEXT, GeneratedSymlinkPath TEXT, GeneratedSymlinkTarget TEXT);

            CREATE TABLE DavNzbFiles (Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL);
            CREATE TABLE DavMultipartFiles (Id TEXT PRIMARY KEY, Metadata TEXT NOT NULL);
            CREATE TABLE DavRarFiles (Id TEXT PRIMARY KEY, RarParts TEXT NOT NULL);

            CREATE TABLE QueueItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder INTEGER NOT NULL DEFAULT 0,
                FileName TEXT NOT NULL, JobName TEXT NOT NULL, NzbFileSize INTEGER NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, Category TEXT NOT NULL, Priority INTEGER NOT NULL,
                PostProcessing INTEGER NOT NULL, PauseUntil TEXT, ArrDownloadId TEXT, ContentGroupKey TEXT, IndexerName TEXT);
            CREATE TABLE QueueNzbContents (Id TEXT PRIMARY KEY, NzbContents TEXT NOT NULL);

            CREATE TABLE HistoryItems (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL, DownloadStatus INTEGER NOT NULL,
                DownloadTimeSeconds INTEGER NOT NULL, FailMessage TEXT, FileName TEXT NOT NULL, JobName TEXT NOT NULL,
                TotalSegmentBytes INTEGER NOT NULL, DownloadDirId TEXT, ArrDownloadId TEXT, ContentGroupKey TEXT,
                IndexerName TEXT, LastPlayedAt INTEGER, NzbBlobId TEXT);

            CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);
            CREATE TABLE Accounts (Type INTEGER NOT NULL, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, RandomSalt TEXT NOT NULL, PRIMARY KEY (Type, Username));
            CREATE UNIQUE INDEX IX_Accounts_SingleAdmin ON Accounts (Type) WHERE Type = 1;

            CREATE TABLE HealthCheckResults (Id TEXT PRIMARY KEY, CreatedAt INTEGER NOT NULL, DavItemId TEXT NOT NULL, Path TEXT NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Message TEXT, JobName TEXT, NzbFileName TEXT);
            CREATE TABLE HealthCheckStats (DateStartInclusive INTEGER NOT NULL, DateEndExclusive INTEGER NOT NULL, Result INTEGER NOT NULL, RepairStatus INTEGER NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY (DateStartInclusive, DateEndExclusive, Result, RepairStatus));
            """);
        return conn;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
