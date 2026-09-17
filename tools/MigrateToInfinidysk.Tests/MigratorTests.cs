using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class MigratorTests
{
    private static SourceSnapshot EmptySnapshot() => new(
        DavItems: [], DavNzbFiles: [], DavMultipartFiles: [], DavRarFiles: [], QueueItems: [],
        QueueNzbContents: [], HistoryItems: [], ConfigItems: [], Accounts: [], HealthCheckResults: [],
        HealthCheckStats: [], LocalLinks: [], AnalysisHistoryItems: [], BandwidthSamples: [],
        MissingArticleEvents: [], MissingArticleSummaries: [], NzbProviderStats: [], ProviderBenchmarkResults: []);

    [Fact]
    public void Run_ObfuscationKeyRow_IsSkippedAndReportedNotImported()
    {
        var mpId = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavMultipartFiles =
            [
                new SourceDavMultipartFile(
                    mpId, AesParams: null, ObfuscationKey: [0xB0, 0x41, 0xC2, 0xCE],
                    FileParts: [new SourceSegmentFilePart(["seg"], 0, 100, 0, 100, null)])
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        Assert.Empty(result.DavMultipartFiles);
        Assert.Equal(1, result.Counts["DavMultipartFiles"].Skipped);
        Assert.Single(result.Archive.SkippedObfuscatedRows);
        Assert.Contains(mpId.ToString(), result.Archive.SkippedObfuscatedRows[0]);
    }

    [Fact]
    public void Run_MultipleAdminsWithoutSelection_FailsWithNoPartialWrite()
    {
        var snapshot = EmptySnapshot() with
        {
            Accounts =
            [
                new SourceAccount(1, "alice", "hash1", "salt1"),
                new SourceAccount(1, "bob", "hash2", "salt2"),
            ],
            DavItems = [new SourceDavItem(Guid.NewGuid(), "abcde", 0, null, "root", null, 1, "/", null, null, null, null, false, null, null)]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(RequestedAdminUsername: null));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("admin-username"));
        // fail loudly, no partial write: nothing at all should be populated
        Assert.Empty(result.DavItems);
        Assert.Empty(result.Accounts);
    }

    [Fact]
    public void Run_MultipleAdminsWithSelection_KeepsOnlyChosenAdmin()
    {
        var snapshot = EmptySnapshot() with
        {
            Accounts =
            [
                new SourceAccount(1, "alice", "hash1", "salt1"),
                new SourceAccount(1, "bob", "hash2", "salt2"),
                new SourceAccount(2, "webdav-user", "hash3", "salt3"),
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(RequestedAdminUsername: "bob"));

        Assert.True(result.Success);
        Assert.Equal(2, result.Accounts.Count);
        Assert.Contains(result.Accounts, a => a is { Type: 1, Username: "bob" });
        Assert.DoesNotContain(result.Accounts, a => a.Username == "alice");
        Assert.Contains(result.Accounts, a => a is { Type: 2, Username: "webdav-user" });
    }

    [Fact]
    public void Run_QueueItems_BackfillsSortOrderPerPriorityGroup()
    {
        var idA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var idB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var snapshot = EmptySnapshot() with
        {
            QueueItems =
            [
                new SourceQueueItem(idA, 1700000000, "a.mkv", "job-a", 100, 100, "movies", 0, 0, null),
                new SourceQueueItem(idB, 1700000100, "b.mkv", "job-b", 200, 200, "movies", 0, 0, null),
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        var a = result.QueueItems.Single(q => q.Id == idA);
        var b = result.QueueItems.Single(q => q.Id == idB);
        Assert.Equal(1024, a.SortOrder);
        Assert.Equal(2048, b.SortOrder);
    }

    [Fact]
    public void Run_DavItem_MapsTypeAndSubTypeAndDefaultsHealthRepairPendingFalse()
    {
        var id = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavItems = [new SourceDavItem(id, "abcde", 0, null, "movie.mkv", 12345, 6 /* MultipartFile */, "/content/movie.mkv", null, null, null, null, false, null, null)]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        var mapped = result.DavItems.Single();
        Assert.Equal(2, mapped.Type);
        Assert.Equal(203, mapped.SubType);
        Assert.False(mapped.HealthRepairPending);
    }

    [Fact]
    public void Run_IncompatibleTables_AreArchivedNotImportedAndCounted()
    {
        var snapshot = EmptySnapshot() with
        {
            LocalLinks = [new IncompatibleTableRow(new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() })],
            BandwidthSamples =
            [
                new IncompatibleTableRow(new Dictionary<string, object?> { ["Id"] = 1 }),
                new IncompatibleTableRow(new Dictionary<string, object?> { ["Id"] = 2 }),
            ],
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        Assert.Equal(1, result.Counts["LocalLinks"].Archived);
        Assert.Equal(2, result.Counts["BandwidthSamples"].Archived);
        Assert.Single(result.Archive.LocalLinks);
        Assert.Equal(2, result.Archive.BandwidthSamples.Count);
    }

    [Fact]
    public void Run_ConfigItems_SkipsLegacyUsenetKeysAndArchivesThem()
    {
        var snapshot = EmptySnapshot() with
        {
            ConfigItems =
            [
                new SourceConfigItem("api.key", "abc"),
                new SourceConfigItem("usenet.host", "news.example.com"),
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.Single(result.ConfigItems);
        Assert.Equal("api.key", result.ConfigItems[0].ConfigName);
        Assert.Single(result.Archive.SkippedConfigItems);
        Assert.Contains(result.Warnings, w => w.Contains("usenet.host"));
    }
}
