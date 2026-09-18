using NzbWebDAV.MigrateToInfinidysk.Mapping;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class ConfigItemFilterTests
{
    [Fact]
    public void Filter_RecognizedKeys_AreCopied()
    {
        var items = new[] { ("api.key", "abc123"), ("webdav.user", "admin"), ("rclone.mount-dir", "/mnt") };

        var result = ConfigItemFilter.Filter(items);

        Assert.Equal(3, result.Copy.Count);
        Assert.Empty(result.Skip);
    }

    [Theory]
    [InlineData("usenet.host")]
    [InlineData("usenet.port")]
    [InlineData("usenet.use-ssl")]
    [InlineData("usenet.connections")]
    [InlineData("usenet.user")]
    [InlineData("usenet.pass")]
    public void Filter_LegacyDiscreteUsenetKeys_AreSkipped(string legacyKey)
    {
        var items = new[] { (legacyKey, "some-value") };

        var result = ConfigItemFilter.Filter(items);

        Assert.Empty(result.Copy);
        Assert.Single(result.Skip);
        Assert.Equal(legacyKey, result.Skip[0].Name);
    }

    [Fact]
    public void Filter_UnknownKey_IsSkipped()
    {
        var items = new[] { ("totally.made.up.key", "x") };

        var result = ConfigItemFilter.Filter(items);

        Assert.Empty(result.Copy);
        Assert.Single(result.Skip);
    }

    [Fact]
    public void Filter_SearchExcludePrefixMatch_IsCopied()
    {
        var items = new[] { ("search.exclude", "*.sample.*") };

        var result = ConfigItemFilter.Filter(items);

        Assert.Single(result.Copy);
    }
}
