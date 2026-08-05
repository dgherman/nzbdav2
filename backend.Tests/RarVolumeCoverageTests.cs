using NzbWebDAV.Queue.FileAggregators;

namespace NzbWebDAV.Tests;

public class RarVolumeCoverageTests
{
    private static RarAggregator.VolumeCoverage Volume(
        long declaredSize,
        long packedBytes,
        bool unknownSize = false,
        bool encrypted = false
    ) => new(declaredSize, unknownSize, packedBytes, encrypted);

    [Fact]
    public void CompleteVolumeSet_IsAccepted()
    {
        // Every volume of a RAR set declares the same full uncompressed size in its file
        // header; the packed bytes each contributes sum to that size when none are missing.
        var volumes = new[]
        {
            Volume(declaredSize: 300, packedBytes: 100),
            Volume(declaredSize: 300, packedBytes: 100),
            Volume(declaredSize: 300, packedBytes: 100),
        };

        RarAggregator.ValidateVolumeCoverage(volumes, "archive.mkv");
    }

    [Fact]
    public void MissingVolumes_AreRejected()
    {
        // This is the Alone S13E04 shape: the headers declare a ~3.7 GB file but only a
        // handful of volumes parsed, so the packed bytes cover a fraction of it.
        var volumes = new[]
        {
            Volume(declaredSize: 3_697_727_697, packedBytes: 52_223_767),
            Volume(declaredSize: 3_697_727_697, packedBytes: 52_223_767),
            Volume(declaredSize: 3_697_727_697, packedBytes: 52_223_767),
        };

        var ex = Assert.Throws<InvalidDataException>(
            () => RarAggregator.ValidateVolumeCoverage(volumes, "archive.mkv"));
        Assert.Contains("archive.mkv", ex.Message);
    }

    [Fact]
    public void SingleVolumeArchive_IsAccepted()
    {
        var volumes = new[] { Volume(declaredSize: 1000, packedBytes: 1000) };

        RarAggregator.ValidateVolumeCoverage(volumes, "archive.mkv");
    }

    [Fact]
    public void PaddingWithinAesBlockTolerance_IsAccepted()
    {
        // Encrypted volumes pad the packed data up to the AES block size, so the packed sum
        // legitimately overshoots the declared size by a few bytes.
        var volumes = new[]
        {
            Volume(declaredSize: 200, packedBytes: 104, encrypted: true),
            Volume(declaredSize: 200, packedBytes: 104, encrypted: true),
        };

        RarAggregator.ValidateVolumeCoverage(volumes, "archive.mkv");
    }

    [Fact]
    public void ConflictingDeclaredSizes_AreRejected()
    {
        var volumes = new[]
        {
            Volume(declaredSize: 300, packedBytes: 100),
            Volume(declaredSize: 900, packedBytes: 200),
        };

        Assert.Throws<InvalidDataException>(
            () => RarAggregator.ValidateVolumeCoverage(volumes, "archive.mkv"));
    }

    [Fact]
    public void UnknownDeclaredSizes_AreAccepted()
    {
        // A volume may set the RAR5 "unpacked size unknown" flag. There is nothing to
        // compare against, so coverage cannot be judged and the set is let through.
        var volumes = new[]
        {
            Volume(declaredSize: long.MaxValue, packedBytes: 100, unknownSize: true),
            Volume(declaredSize: long.MaxValue, packedBytes: 100, unknownSize: true),
        };

        RarAggregator.ValidateVolumeCoverage(volumes, "archive.mkv");
    }

    [Fact]
    public void UnknownDeclaredSizeOnSomeVolumes_StillChecksTheKnownOne()
    {
        var volumes = new[]
        {
            Volume(declaredSize: 300, packedBytes: 100),
            Volume(declaredSize: long.MaxValue, packedBytes: 100, unknownSize: true),
        };

        Assert.Throws<InvalidDataException>(
            () => RarAggregator.ValidateVolumeCoverage(volumes, "archive.mkv"));
    }

    [Fact]
    public void NoVolumes_IsAccepted()
    {
        RarAggregator.ValidateVolumeCoverage([], "archive.mkv");
    }
}
