using System;
using System.IO;
using System.Linq;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Shared fixture helpers for issue #28 (password-protected <c>-hp</c> RAR imports failing on
/// NZB segment-size misalignment). Backs both <see cref="Issue28RarStreamMisalignmentTests"/>
/// (the <c>RarUtil</c>/<c>NzbFileStream</c> seam) and <see cref="Issue28RarProcessorIntegrationTests"/>
/// (the real <c>RarProcessor</c> production seam, over a real mock-NNTP server).
/// </summary>
internal static class Issue28Fixtures
{
    public const string Password = "testpass";

    /// <summary>Random payload size fed to `rar a -hptestpass -v1000000b -ma5 -m0 -ep` to build
    /// the fixtures in <c>Fixtures/issue28</c>.</summary>
    public const long DeclaredUncompressedSize = 2_600_000;

    public static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "backend.Tests", "Fixtures", "issue28")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "backend.Tests", "Fixtures", "issue28");
    }

    public static byte[] Volume(string name) => File.ReadAllBytes(Path.Combine(FixtureDir(), name));

    /// <summary>
    /// Non-uniform layout matching the issue's trigger: <paramref name="fullCount"/> segments of
    /// <paramref name="fullSize"/> bytes then one short remainder segment. The sizes sum exactly to
    /// <paramref name="totalBytes"/>.
    /// </summary>
    public static long[] NonUniformLayout(long totalBytes, int fullCount, int fullSize)
    {
        var sizes = new long[fullCount + 1];
        for (var i = 0; i < fullCount; i++) sizes[i] = fullSize;
        sizes[fullCount] = totalBytes - (long)fullCount * fullSize;
        Assert.True(sizes[fullCount] > 0 && sizes[fullCount] < fullSize,
            "layout must end with a genuinely short segment");
        Assert.Equal(totalBytes, sizes.Sum());
        return sizes;
    }

    /// <summary>What the pre-fix <c>GetFastFileStream</c> fabricated: uniform size, remainder last.</summary>
    public static long[] FabricateUniformTable(long fileSize, int segmentCount)
    {
        var sizes = new long[segmentCount];
        var avg = fileSize / segmentCount;
        Array.Fill(sizes, avg);
        sizes[^1] = fileSize - avg * (segmentCount - 1);
        return sizes;
    }
}
