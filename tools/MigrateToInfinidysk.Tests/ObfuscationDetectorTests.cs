using NzbWebDAV.MigrateToInfinidysk.Mapping;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class ObfuscationDetectorTests
{
    [Fact]
    public void IsUnmigratable_ExplicitObfuscationKey_ReturnsTrue()
    {
        var result = ObfuscationDetector.IsUnmigratable(obfuscationKey: [0xB0, 0x41, 0xC2, 0xCE], wasRarSourced: false);

        Assert.True(result);
    }

    [Fact]
    public void IsUnmigratable_NullKeyButRarSourced_ReturnsTrue()
    {
        // nzbdav2's RarDeobfuscationStream falls back to content-sniffed default-key
        // auto-detection when ObfuscationKey is null; that detection is content-based
        // and cannot be faithfully replicated from DB rows alone, so RAR-sourced rows
        // with no recorded key are conservatively flagged too.
        var result = ObfuscationDetector.IsUnmigratable(obfuscationKey: null, wasRarSourced: true);

        Assert.True(result);
    }

    [Fact]
    public void IsUnmigratable_NullKeyAndNotRarSourced_ReturnsFalse()
    {
        var result = ObfuscationDetector.IsUnmigratable(obfuscationKey: null, wasRarSourced: false);

        Assert.False(result);
    }
}
