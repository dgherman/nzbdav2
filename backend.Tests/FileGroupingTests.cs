using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests;

/// <summary>
/// Grouping decides which files a single processor sees. Getting it wrong for RAR silently
/// mounts a fraction of a video, so pin the shapes that matter.
/// </summary>
public class FileGroupingTests
{
    /// <summary>
    /// Groups a set of RAR volumes exactly as <c>GetFileProcessors</c> does.
    /// </summary>
    private static List<List<string>> GroupRarVolumes(params string[] fileNames) =>
        fileNames
            .GroupBy(x => (
                Type: QueueItemProcessor.GetGroupType(x, isRar: true, isSevenZip: false),
                Key: QueueItemProcessor.GetGroupKey(x, isRar: true, isSevenZip: false)))
            .Select(x => x.ToList())
            .ToList();

    [Fact]
    public void RarVolumesWithPerVolumeObfuscatedNames_FormOneGroup()
    {
        // Real subjects from the Alone S13E04 post: one archive, a different random base name
        // on every volume. Base-name grouping made this 71 groups of one volume each.
        var groups = GroupRarVolumes(
            "_08hH_xl1BZJZOoFgsw3zC0WNJUV8.part23.rar",
            "-G5FSfxzoPmuWdRg3.part51.rar",
            "0aQ9z8aBiVFRpGQlg4U.part63.rar",
            "3FCxz9jDBxicI0mJApl5iur.part37.rar");

        Assert.Single(groups);
        Assert.Equal(4, groups[0].Count);
    }

    [Fact]
    public void RarVolumesWithConsistentNames_AlsoFormOneGroup()
    {
        var groups = GroupRarVolumes(
            "52ebd92923cd4d82b40850732ceb208d.part01.rar",
            "52ebd92923cd4d82b40850732ceb208d.part02.rar",
            "52ebd92923cd4d82b40850732ceb208d.part03.rar");

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void OldStyleRarVolumes_FormOneGroup()
    {
        var groups = GroupRarVolumes("release.rar", "release.r00", "release.r01", "release.r02");

        Assert.Single(groups);
        Assert.Equal(4, groups[0].Count);
    }

    [Theory]
    [InlineData("release.part01.rar", "rar")]
    [InlineData("release.r07", "rar")]
    [InlineData("release.7z.001", "7z")]
    [InlineData("movie.mkv.001", "multipart-mkv")]
    [InlineData("release.nfo", "other")]
    public void FilesAreClassifiedByExtension(string fileName, string expectedType)
    {
        Assert.Equal(expectedType,
            QueueItemProcessor.GetGroupType(fileName, isRar: false, isSevenZip: false));
    }

    [Fact]
    public void RarMagic_OutranksAMisleadingExtension()
    {
        // Deobfuscation sometimes lands a RAR volume under a name with no archive extension.
        Assert.Equal("rar",
            QueueItemProcessor.GetGroupType("something.bin", isRar: true, isSevenZip: false));
    }

    [Fact]
    public void SevenZipSets_StayGroupedByName()
    {
        // SevenZipProcessor splices its whole list into one archive, so two distinct sets must
        // not land in the same group.
        var groups = new[]
            {
                "movie-a.7z.001", "movie-a.7z.002",
                "movie-b.7z.001", "movie-b.7z.002",
            }
            .GroupBy(x => (
                Type: QueueItemProcessor.GetGroupType(x, isRar: false, isSevenZip: true),
                Key: QueueItemProcessor.GetGroupKey(x, isRar: false, isSevenZip: true)))
            .Select(x => x.ToList())
            .ToList();

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Count));
    }

    [Fact]
    public void MultipartMkvSets_StayGroupedByName()
    {
        // Same reason as 7z: MultipartMkvProcessor concatenates its list into a single file.
        var groups = new[]
            {
                "episode-1.mkv.001", "episode-1.mkv.002",
                "episode-2.mkv.001", "episode-2.mkv.002",
            }
            .GroupBy(x => (
                Type: QueueItemProcessor.GetGroupType(x, isRar: false, isSevenZip: false),
                Key: QueueItemProcessor.GetGroupKey(x, isRar: false, isSevenZip: false)))
            .Select(x => x.ToList())
            .ToList();

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Count));
    }
}
