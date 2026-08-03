using NzbWebDAV.Utils;
using Xunit;

namespace NzbWebDAV.Tests;

public class FileFilterUtilTests
{
    private const long FeatureSize = 8_000_000_000; // 8 GB main video
    private const long SampleSize = 40_000_000;     // 40 MB sample (0.5% of the feature)

    [Theory]
    [InlineData("sample.mkv")]
    [InlineData("Sample.mkv")]
    [InlineData("Show.S01E01.1080p.WEB.sample.mkv")]
    [InlineData("sample-Show.S01E01.1080p.mkv")]
    [InlineData("Show.S01E01 (sample).mkv")]
    [InlineData("Show.S01E01.samples.mkv")]
    public void IsSampleFile_SmallVideoNamedSample_IsFiltered(string filename)
    {
        // The extension blacklist can never express this: a sample carries the same extension as the
        // feature. Left in place it becomes an *.strm of identical size to the real one, which is
        // exactly what Sonarr's own sample heuristic then fails to tell apart.
        Assert.True(FileFilterUtil.IsSampleFile(filename, SampleSize, FeatureSize));
    }

    [Fact]
    public void IsSampleFile_LargestVideoInTheRelease_IsNeverFiltered()
    {
        // "Free Samples (2012)" is a real film. Its video is the largest file in its own release, so
        // the ratio check keeps it — this is what lets the filter default to on.
        Assert.False(FileFilterUtil.IsSampleFile("Free.Samples.2012.1080p.BluRay.mkv", FeatureSize, FeatureSize));
    }

    [Fact]
    public void IsSampleFile_SampleOnlyRelease_IsNeverFiltered()
    {
        // A release whose only video happens to be named `sample` compares against itself, so nothing
        // is removed and the item still validates as importable media rather than failing outright.
        Assert.False(FileFilterUtil.IsSampleFile("sample.mkv", SampleSize, SampleSize));
    }

    [Fact]
    public void IsSampleFile_LargeExtraNamedSample_IsNotFiltered()
    {
        // Half the size of the feature is not a sample by any reasonable reading, whatever it is
        // called. The 20% ratio is the whole safety margin, so pin it.
        Assert.False(FileFilterUtil.IsSampleFile("Show.sample.mkv", FeatureSize / 2, FeatureSize));
    }

    [Theory]
    [InlineData("Resampled.Audio.mkv")]
    [InlineData("Oversampling.mkv")]
    public void IsSampleFile_WordEmbeddedInALongerWord_IsNotFiltered(string filename)
    {
        Assert.False(FileFilterUtil.IsSampleFile(filename, SampleSize, FeatureSize));
    }

    [Theory]
    // Real subjects taken from a 14-day export of production history (2026-07-28). Both naming
    // conventions appear in the wild and both must be caught.
    [InlineData("dating.naked.uk.s01e07.italian.1080p.web.h264-neurosis-sample.mkv")]
    [InlineData("sample-dating.naked.uk.s01e07.1080p.web.h264-bussy.mkv")]
    [InlineData("love.on.the.spectrum.u.s.s03e01.polish.1080p.web.h264-flame-sample.mkv")]
    public void IsSampleFile_RealWorldSampleNames_AreFiltered(string filename)
    {
        // Sizes from the same export: ~75 MB samples against a ~1.5 GB feature.
        Assert.True(FileFilterUtil.IsSampleFile(filename, 75 * 1024 * 1024L, 1536 * 1024 * 1024L));
    }

    [Fact]
    public void IsSampleFile_RarPackedRelease_KeepsTheSampleUntilTheArchiveIsExpanded()
    {
        // Pins a real property of the corpus above: in a RAR-packed release the feature lives in
        // .rar/.r00 parts, which are not video files, so before extraction the sample is the only
        // video and compares against itself. The filter runs after the archive processors have
        // mounted the extracted feature, which is what makes the ratio meaningful — if that ordering
        // ever changes, every RAR release's sample starts surviving and this test says why.
        const long sampleSize = 75 * 1024 * 1024L;
        Assert.False(FileFilterUtil.IsSampleFile(
            "dating.naked.uk.s01e07.italian.1080p.web.h264-neurosis-sample.mkv",
            sampleSize, largestVideoFileSize: sampleSize));
    }

    [Fact]
    public void IsSampleFile_NonVideoFile_IsNotFiltered()
    {
        // Non-video files go through the extension blacklist instead; a sample .nfo or a subtitle
        // must not be silently dropped by the video heuristic.
        Assert.False(FileFilterUtil.IsSampleFile("sample.srt", 1024, FeatureSize));
    }

    [Fact]
    public void IsSampleFile_WithoutAKnownSize_IsNotFiltered()
    {
        // No size means no ratio, and guessing would risk deleting the feature.
        Assert.False(FileFilterUtil.IsSampleFile("sample.mkv", null, FeatureSize));
        Assert.False(FileFilterUtil.IsSampleFile("sample.mkv", SampleSize, largestVideoFileSize: 0));
    }

    [Theory]
    [InlineData("Show.S01E01.trailer.mkv", "*trailer*", true)]
    [InlineData("Show.S01E01.mkv", "*trailer*", false)]
    [InlineData("proof.jpg", "proof.???", true)]
    [InlineData("PROOF.JPG", "proof.jpg", true)]
    [InlineData("notproof.jpg", "proof.jpg", false)]
    public void MatchesAnyGlob_MatchesTheFilenameCaseInsensitively(string filename, string glob, bool expected)
    {
        Assert.Equal(expected, FileFilterUtil.MatchesAnyGlob(filename, new[] { glob }));
    }

    [Fact]
    public void MatchesAnyGlob_WithNoPatterns_MatchesNothing()
    {
        // The default config is empty, so this is the path every existing install takes.
        Assert.False(FileFilterUtil.MatchesAnyGlob("anything.mkv", System.Array.Empty<string>()));
    }

    [Fact]
    public void MatchesAnyGlob_IgnoresTheDirectoryPart()
    {
        // A release folder called `Sample.Release.2024` must not make every file inside it match.
        Assert.False(FileFilterUtil.MatchesAnyGlob("/content/sample.release/Show.mkv", new[] { "*sample*" }));
        Assert.True(FileFilterUtil.MatchesAnyGlob("/content/show/Show.sample.mkv", new[] { "*sample*" }));
    }
}
