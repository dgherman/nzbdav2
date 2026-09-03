using System.Collections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NzbWebDAV.Tests;

public class SevenZipAesFallbackTests
{
    private const string Password = "issue31-password";

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "backend.Tests", "Fixtures")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string FixturePath(string relativePath) =>
        Path.Combine(RepositoryRoot(), "backend.Tests", "Fixtures", relativePath);

    private static string SharpCompressArchivePath(string fileName) =>
        Path.Combine(
            RepositoryRoot(),
            "backend",
            "Libs",
            "SharpCompress",
            "tests",
            "TestArchives",
            "Archives",
            fileName
        );

    [Fact]
    public void EncryptedStoredEntry_ExposesMetadataWithoutThrowing()
    {
        using var stream = File.OpenRead(FixturePath("issue31/encrypted-stored.7z"));
        using var archive = SevenZipArchive.Open(stream, new ReaderOptions { Password = Password });
        var entry = Assert.Single(archive.Entries, entry => entry.Key == "payload.txt");

        Assert.Equal(CompressionType.None, entry.CompressionType);
        Assert.True(entry.IsEncrypted);
        Assert.NotNull(entry.GetAesCoderInfoProps());
    }

    [Fact]
    public void SevenZipUtil_BuildsEncryptedStoredEntryWithAesParameters()
    {
        using var stream = File.OpenRead(FixturePath("issue31/encrypted-stored.7z"));

        var entries = SevenZipUtil.GetSevenZipEntries(stream, Password);
        var entry = Assert.Single(entries, entry => entry.PathWithinArchive == "payload.txt");

        Assert.Equal(CompressionType.None, entry.CompressionType);
        Assert.True(entry.IsEncrypted);
        Assert.NotNull(entry.AesParams);
    }

    [Fact]
    public void AesInvalidFormatException_ReachesApplicationFallback()
    {
        using var stream = File.OpenRead(FixturePath("issue31/encrypted-stored.7z"));
        using var archive = SevenZipArchive.Open(stream, new ReaderOptions { Password = Password });
        var entry = Assert.Single(archive.Entries);
        var coders = (IList)entry
            .GetReflectionProperty("FilePart")!
            .GetReflectionProperty("Folder")!
            .GetReflectionField("_coders")!;

        Assert.Equal(2, coders.Count);
        coders.RemoveAt(1); // Leave the fixture's AES coder as the unsupported primary coder.

        Assert.Throws<InvalidFormatException>(() => entry.CompressionType);
        Assert.Equal(CompressionType.None, entry.GetCompressionType());
    }

    [Fact]
    public void StreamlessEntry_HasNoCompressionAndIsNotEncrypted()
    {
        using var stream = File.OpenRead(FixturePath("issue31/empty-file.7z"));
        using var archive = SevenZipArchive.Open(stream);
        var entry = Assert.Single(archive.Entries, entry => entry.Key == "empty.txt");

        Assert.False(entry.IsEncrypted);
        Assert.Equal(CompressionType.None, entry.CompressionType);
    }

    [Theory]
    [InlineData("7Zip.Copy.7z", CompressionType.None)]
    [InlineData("7Zip.LZMA.7z", CompressionType.LZMA)]
    [InlineData("7Zip.LZMA2.7z", CompressionType.LZMA)]
    [InlineData("7Zip.PPMd.7z", CompressionType.PPMd)]
    [InlineData("7Zip.BZip2.7z", CompressionType.BZip2)]
    public void NonEncryptedCompressionTypes_AreUnchanged(
        string archiveName,
        CompressionType expectedCompressionType
    )
    {
        using var stream = File.OpenRead(SharpCompressArchivePath(archiveName));
        using var archive = SevenZipArchive.Open(stream);

        Assert.All(
            archive.Entries.Where(entry => !entry.IsDirectory),
            entry => Assert.Equal(expectedCompressionType, entry.CompressionType)
        );
    }

    [Fact]
    public void UnknownCoder_StillThrowsInvalidFormatException()
    {
        using var stream = File.OpenRead(SharpCompressArchivePath("7Zip.ZSTD.7z"));
        using var archive = SevenZipArchive.Open(stream);
        var entry = archive.Entries.First(entry => !entry.IsDirectory);

        Assert.Throws<InvalidFormatException>(() => entry.CompressionType);
    }
}
