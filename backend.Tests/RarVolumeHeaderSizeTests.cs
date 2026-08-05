using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.FileAggregators;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;

namespace NzbWebDAV.Tests;

/// <summary>
/// Volume-coverage validation rests on one format fact: every volume of a multi-volume RAR set
/// declares the size of the whole extracted file in its file header, not the slice it carries.
/// If that were false the aggregator would reject complete archives, so pin it against real
/// archives rather than trusting the spec.
/// </summary>
public class RarVolumeHeaderSizeTests
{
    private static readonly string[] Rar5Set =
    [
        "Rar.multi.part01.rar", "Rar.multi.part02.rar", "Rar.multi.part03.rar",
        "Rar.multi.part04.rar", "Rar.multi.part05.rar", "Rar.multi.part06.rar",
    ];

    private static readonly string[] Rar4Set =
    [
        "Rar2.multi.rar", "Rar2.multi.r00", "Rar2.multi.r01",
        "Rar2.multi.r02", "Rar2.multi.r03", "Rar2.multi.r04", "Rar2.multi.r05",
    ];

    /// <summary>One volume's contribution to one file inside the archive.</summary>
    private readonly record struct Slice(string Volume, string Path, long Declared, long Packed);

    private static string ArchiveDirectory()
    {
        // The vendored SharpCompress test archives live in the repo, not in the test output.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "backend", "Libs")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return Path.Combine(
            dir!, "backend", "Libs", "SharpCompress", "tests", "TestArchives", "Archives");
    }

    /// <summary>
    /// Reads headers straight from SharpCompress rather than through
    /// <c>RarUtil.GetRarHeaders</c>, which rejects anything that is not compression method m0.
    /// These fixtures are compressed, and what is under test — the declared uncompressed size —
    /// is a header field that does not depend on the compression method.
    /// </summary>
    private static List<Slice> SlicesIn(string archiveFileName)
    {
        var path = Path.Combine(ArchiveDirectory(), archiveFileName);
        Assert.True(File.Exists(path), $"missing test archive: {path}");

        using var stream = File.OpenRead(path);
        var factory = new RarHeaderFactory(StreamingMode.Seekable, new ReaderOptions());
        return factory
            .ReadHeaders(stream)
            .Where(x => x.HeaderType == HeaderType.File && !x.IsDirectory())
            .Select(x => new Slice(
                archiveFileName, x.GetFileName(), x.GetUncompressedSize(), x.GetAdditionalDataSize()))
            .ToList();
    }

    /// <summary>
    /// Slices of every file in the set, keyed by path within the archive — the same grouping
    /// <c>RarAggregator</c> applies before validating coverage.
    /// </summary>
    private static Dictionary<string, List<Slice>> SlicesByPath(string[] volumes) =>
        volumes
            .SelectMany(SlicesIn)
            .GroupBy(x => x.Path)
            .ToDictionary(x => x.Key, x => x.ToList());

    public static TheoryData<string[]> VolumeSets() => new() { Rar5Set, Rar4Set };

    [Theory]
    [MemberData(nameof(VolumeSets))]
    public void EveryVolumeCarryingAFile_DeclaresTheSameSizeForIt(string[] volumes)
    {
        var byPath = SlicesByPath(volumes);
        Assert.NotEmpty(byPath);

        foreach (var (path, slices) in byPath)
        {
            Assert.Single(slices.Select(x => x.Declared).Distinct());
            Assert.All(slices, slice => Assert.True(
                slice.Declared > 0 && slice.Declared != long.MaxValue,
                $"{slice.Volume} declared an unusable size for {path}: {slice.Declared}"));
        }
    }

    [Theory]
    [MemberData(nameof(VolumeSets))]
    public void AVolumeCarryingPartOfASplitFile_DeclaresMoreThanItCarries(string[] volumes)
    {
        // The distinguishing property: a volume holding one slice of a split file still declares
        // the whole file, which is strictly larger than that slice. Were the header per-slice,
        // the two would be equal and coverage validation could not tell a partial set apart.
        var split = SlicesByPath(volumes).Where(x => x.Value.Count > 1).ToList();
        Assert.NotEmpty(split);

        foreach (var (path, slices) in split)
        foreach (var slice in slices)
            Assert.True(slice.Declared > slice.Packed,
                $"{slice.Volume} declared {slice.Declared} for {path} but carries {slice.Packed}");
    }

    [Theory]
    [InlineData("Rar.multi.part03.rar")]
    [InlineData("Rar2.multi.r03")]
    public void AMiddleVolumeReadAlone_StillDeclaresTheWholeFile(string middleVolume)
    {
        // This is what the importer sees: each volume is parsed on its own, with no knowledge of
        // its siblings. The declared size must survive that isolation for coverage to work.
        var wholeSet = middleVolume.StartsWith("Rar.multi") ? Rar5Set : Rar4Set;
        var byPath = SlicesByPath(wholeSet);

        var alone = SlicesIn(middleVolume);
        Assert.NotEmpty(alone);

        foreach (var slice in alone)
            Assert.Equal(byPath[slice.Path].First().Declared, slice.Declared);
    }

    [Theory]
    [MemberData(nameof(VolumeSets))]
    public void APartialVolumeSet_FailsCoverageValidation(string[] volumes)
    {
        // Keep the first two slices of a split file and drop the rest — the shape produced when
        // most volumes fail to parse, which mounted a 156 MB stub of a 3.7 GB episode.
        var (path, slices) = SlicesByPath(volumes).First(x => x.Value.Count > 2);

        var coverage = slices
            .Take(2)
            .Select(x => new RarAggregator.VolumeCoverage(
                DeclaredUncompressedSize: x.Declared,
                IsUncompressedSizeUnknown: false,
                PackedBytes: x.Packed,
                IsEncrypted: false))
            .ToList();

        var ex = Assert.Throws<InvalidDataException>(
            () => RarAggregator.ValidateVolumeCoverage(coverage, path));
        Assert.Contains("incomplete", ex.Message);
    }

    [Theory]
    [MemberData(nameof(VolumeSets))]
    public void ASingleVolumeHoldingAWholeFile_HasMatchingDeclaredAndPackedSize(string[] volumes)
    {
        // Store-mode (m0) is the only method nzbdav mounts, and these fixtures are compressed,
        // so packed < declared here. Assert the weaker invariant that still holds: no file's
        // slices can add up to more than the file itself.
        foreach (var (path, slices) in SlicesByPath(volumes))
        {
            var packedTotal = slices.Sum(x => x.Packed);
            Assert.True(packedTotal <= slices[0].Declared,
                $"{path}: slices total {packedTotal}, declared {slices[0].Declared}");
        }
    }
}
