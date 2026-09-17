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
            DavItems = [new SourceDavItem(mpId, "abcde", 0, null, "movie.mkv", 1000, 6, "/content/movie.mkv", null, null, null, null, false, null, null)],
            DavMultipartFiles =
            [
                new SourceDavMultipartFile(
                    mpId, AesParams: null, ObfuscationKey: [0xB0, 0x41, 0xC2, 0xCE],
                    FileParts: [new SourceSegmentFilePart(["seg-1", "seg-2"], 0, 100, 0, 100, null)])
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        Assert.Empty(result.DavMultipartFiles);
        Assert.Equal(1, result.Counts["DavMultipartFiles"].Skipped);

        // The file simply doesn't appear in the target library: its DavItems row is skipped
        // too, not just its (unmigratable) DavMultipartFiles payload.
        Assert.Empty(result.DavItems);
        Assert.Equal(1, result.Counts["DavItems"].Skipped);

        // The archive carries a full recoverable payload for manual reprocessing, not just a
        // diagnostic message: the DavItem id, the key itself, and the source metadata JSON
        // (which must still contain the actual segment ids a human would need).
        var archived = Assert.Single(result.Archive.SkippedObfuscatedFiles);
        Assert.Equal(mpId, archived.DavItemId);
        Assert.Equal(Convert.ToBase64String([0xB0, 0x41, 0xC2, 0xCE]), archived.ObfuscationKeyBase64);
        Assert.Contains("seg-1", archived.SourceMetadataJson);
        Assert.Contains("seg-2", archived.SourceMetadataJson);
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

    [Fact]
    public void Run_DavNzbFileWithSegmentFallbacks_ArchivesAlignedFallbackIds()
    {
        var nzbId = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavNzbFiles =
            [
                new SourceDavNzbFile(
                    nzbId,
                    SegmentIds: ["seg-1", "seg-2", "seg-3"],
                    SegmentFallbacks: new Dictionary<int, string[]> { [1] = ["fallback-for-seg-2"] })
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        // SegmentIds itself still copies through into the live DavNzbFiles row...
        Assert.Single(result.DavNzbFiles);
        Assert.Contains("seg-1", result.DavNzbFiles[0].SegmentIdsJson);
        // ...but the fallback ids (infinidysk has no SQL column for these on DavNzbFiles) are
        // preserved in the archive, aligned by segment index, not silently dropped.
        var archived = Assert.Single(result.Archive.DavNzbFileFallbackIds);
        Assert.Equal(nzbId, archived.DavNzbFileId);
        Assert.Equal(3, archived.SegmentFallbackIds.Length);
        Assert.Empty(archived.SegmentFallbackIds[0]);
        Assert.Equal(["fallback-for-seg-2"], archived.SegmentFallbackIds[1]);
        Assert.Empty(archived.SegmentFallbackIds[2]);
    }

    [Fact]
    public void Run_NativeDavMultipartFileRow_WithNullObfuscationKey_MigratesNormally()
    {
        // A row that lives directly in nzbdav2's DavMultipartFiles table (never went through the
        // legacy DavRarFiles table) with a null ObfuscationKey is the common case - most
        // multipart files never went through RAR at all (MultipartMkvProcessor and
        // SevenZipProcessor both produce multipart rows too, and neither sets an obfuscation
        // key). Unlike a RAR-converted row, nzbdav2 has no content-sniffed-obfuscation risk for
        // these, so it must migrate normally, not be conservatively skipped.
        var mpId = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavItems = [new SourceDavItem(mpId, "abcde", 0, null, "movie.mkv", 1000, 6, "/content/movie.mkv", null, null, null, null, false, null, null)],
            DavMultipartFiles =
            [
                new SourceDavMultipartFile(
                    mpId, AesParams: null, ObfuscationKey: null,
                    FileParts: [new SourceSegmentFilePart(["seg"], 0, 100, 0, 100, null)])
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        Assert.Single(result.DavMultipartFiles);
        Assert.Equal(0, result.Counts["DavMultipartFiles"].Skipped);
        Assert.Empty(result.Archive.SkippedObfuscatedFiles);
        Assert.Single(result.DavItems);
    }

    [Fact]
    public void Run_RarConvertedRow_WithNullObfuscationKey_StillSkippedAndArchived()
    {
        // Unlike a native DavMultipartFiles row, a legacy DavRarFiles row's null ObfuscationKey
        // is NOT proof the content is unobfuscated - nzbdav2 detects RAR obfuscation by
        // content-sniffing at read time (RarDeobfuscationStream), not from a stored flag. This
        // must keep going through the conservative skip-and-archive path regardless of
        // key-nullness - a permanent limitation (infinidysk has no XOR-deobfuscation support),
        // not something this round fixes.
        var rarId = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavItems = [new SourceDavItem(rarId, "abcde", 0, null, "movie.mkv", 1000, 4 /* legacy RarFile */, "/content/movie.mkv", null, null, null, null, false, null, null)],
            DavRarFiles =
            [
                new SourceDavRarFile(rarId,
                    [new SourceDavRarPart(["seg-1"], PartSize: 100, Offset: 0, ByteCount: 100, ObfuscationKey: null)])
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        Assert.Empty(result.DavMultipartFiles);
        Assert.Equal(1, result.Counts["DavMultipartFiles"].Skipped);
        Assert.Single(result.Archive.SkippedObfuscatedFiles);
        Assert.Empty(result.DavItems);
    }

    [Fact]
    public void Run_DavNzbFileWithFallbacksAndValidFileSize_WrapsAsPlayableMultipartRow()
    {
        // Fixes the "fallback IDs unreachable at playback" gap: instead of writing a DavNzbFiles
        // row and archiving the fallback IDs where nothing at playback time ever reads them, the
        // file is wrapped as a single-part DavMultipartFiles row - infinidysk's
        // FilePart.SegmentFallbackIds is read at playback, unlike anything on DavNzbFiles.
        var nzbId = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavItems = [new SourceDavItem(nzbId, "abcde", 0, null, "movie.mkv", 5000, 3 /* NzbFile */, "/content/movie.mkv", null, null, null, null, false, null, null)],
            DavNzbFiles =
            [
                new SourceDavNzbFile(
                    nzbId,
                    SegmentIds: ["seg-1", "seg-2", "seg-3"],
                    SegmentFallbacks: new Dictionary<int, string[]> { [1] = ["fallback-for-seg-2"] })
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        // no DavNzbFiles row at all - and nothing archived-only either, since it's now playable
        Assert.Empty(result.DavNzbFiles);
        Assert.Empty(result.Archive.DavNzbFileFallbackIds);

        var wrapped = Assert.Single(result.DavMultipartFiles);
        Assert.Equal(nzbId, wrapped.Id);
        Assert.Contains("seg-1", wrapped.MetadataJson);
        Assert.Contains("fallback-for-seg-2", wrapped.MetadataJson);
        // byte range must cover the full source FileSize
        Assert.Contains("\"EndExclusive\":5000", wrapped.MetadataJson);

        var davItem = Assert.Single(result.DavItems);
        Assert.Equal(2, davItem.Type);
        Assert.Equal(203, davItem.SubType);
    }

    [Fact]
    public void Run_DavNzbFileWithFallbacksButNoValidFileSize_FallsBackToArchiveOnlyWithWarning()
    {
        // No matching DavItems row (or a null/non-positive FileSize on one) means there's no
        // real byte range to wrap around - falls back to the previous archive-only behavior
        // rather than inventing a fake size, and says so in a warning.
        var nzbId = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavItems = [new SourceDavItem(nzbId, "abcde", 0, null, "movie.mkv", null /* no FileSize */, 3, "/content/movie.mkv", null, null, null, null, false, null, null)],
            DavNzbFiles =
            [
                new SourceDavNzbFile(
                    nzbId, SegmentIds: ["seg-1", "seg-2"],
                    SegmentFallbacks: new Dictionary<int, string[]> { [0] = ["fallback-for-seg-1"] })
            ]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        Assert.Single(result.DavNzbFiles);
        Assert.Empty(result.DavMultipartFiles);
        Assert.Single(result.Archive.DavNzbFileFallbackIds);
        Assert.Contains(result.Warnings, w => w.Contains(nzbId.ToString()) && w.Contains("FileSize"));

        var davItem = Assert.Single(result.DavItems);
        Assert.Equal(201, davItem.SubType); // unwrapped NzbFile default, not 203
    }

    [Fact]
    public void Run_DavNzbFileWithoutFallbacksButValidFileSize_StillWrapsAsMultipartRow()
    {
        // The wrap applies to every NZB file with a valid FileSize, not only ones with fallback
        // data to preserve - a row with no fallbacks just gets a null SegmentFallbackIds.
        var nzbId = Guid.NewGuid();
        var snapshot = EmptySnapshot() with
        {
            DavItems = [new SourceDavItem(nzbId, "abcde", 0, null, "movie.mkv", 2000, 3, "/content/movie.mkv", null, null, null, null, false, null, null)],
            DavNzbFiles = [new SourceDavNzbFile(nzbId, SegmentIds: ["seg-1"], SegmentFallbacks: null)]
        };

        var result = Migrator.Run(snapshot, new MigrationOptions(null));

        Assert.True(result.Success);
        Assert.Empty(result.DavNzbFiles);
        Assert.Single(result.DavMultipartFiles);
        Assert.Equal(203, result.DavItems.Single().SubType);
    }
}
