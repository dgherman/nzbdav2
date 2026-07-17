using NzbWebDAV.Services;

namespace NzbWebDAV.Tests;

public class StreamSessionRegistryTests
{
    private static StreamSessionRegistry NewRegistry(TimeSpan? ttl = null)
        => new StreamSessionRegistry { Ttl = ttl ?? TimeSpan.FromSeconds(15) };

    [Fact]
    public void Touch_AddsSession_AndGetActiveReturnsIt()
    {
        var reg = NewRegistry();
        var id = Guid.NewGuid();

        reg.Touch(id, "Movie.mkv", "movie", currentBytePosition: 100, fileSize: 1000);

        var sessions = reg.GetActiveSessions();
        Assert.Single(sessions);
        Assert.Equal(id, sessions[0].DavItemId);
        Assert.Equal("Movie.mkv", sessions[0].FileName);
        Assert.Equal(100, sessions[0].CurrentBytePosition);
        Assert.Equal(1000, sessions[0].FileSize);
    }

    [Fact]
    public void Touch_SameDavItem_CollapsesToOneSession_AndUpdatesPosition()
    {
        var reg = NewRegistry();
        var id = Guid.NewGuid();

        reg.Touch(id, "Movie.mkv", "movie", 100, 1000);
        reg.Touch(id, "Movie.mkv", "movie", 500, 1000);

        var sessions = reg.GetActiveSessions();
        Assert.Single(sessions);
        Assert.Equal(500, sessions[0].CurrentBytePosition);
    }

    [Fact]
    public void GetActiveSessions_ExcludesEntriesOlderThanTtl()
    {
        var reg = NewRegistry(TimeSpan.FromMilliseconds(1));
        reg.Touch(Guid.NewGuid(), "Old.mkv", "old", 1, 10);

        Thread.Sleep(20);

        Assert.Empty(reg.GetActiveSessions());
    }

    [Fact]
    public void Current_PointsAtMostRecentlyConstructedInstance()
    {
        var reg = NewRegistry();
        Assert.Same(reg, StreamSessionRegistry.Current);
    }

    [Fact]
    public void BuildDtos_MapsProviderIndexToHost_AndComputesProgress()
    {
        var id = Guid.NewGuid();
        var sessions = new[]
        {
            new ActiveStreamSnapshot(id, "Movie.mkv", "movie", CurrentBytePosition: 500, FileSize: 1000)
        };

        Dictionary<int, NzbWebDAV.Database.Models.NzbProviderStats> Lookup(string key) => new()
        {
            [0] = new() { JobName = key, ProviderIndex = 0, TotalBytes = 1_200_000_000 },
            [1] = new() { JobName = key, ProviderIndex = 1, TotalBytes = 380_000_000 },
        };
        var hosts = new[] { "news.frugalusenet.com", "news.newshosting.com" };

        var dtos = StreamSessionRegistry.BuildDtos(sessions, Lookup, hosts);

        Assert.Single(dtos);
        Assert.Equal(50, dtos[0].ProgressPercent);
        Assert.Equal(2, dtos[0].Providers.Count);
        // Sorted by bytes desc: frugalusenet first.
        Assert.Equal("news.frugalusenet.com", dtos[0].Providers[0].Host);
        Assert.Equal(1_200_000_000, dtos[0].Providers[0].TotalBytes);
    }

    [Fact]
    public void BuildDtos_DropsProviderIndicesOutsideCurrentConfig()
    {
        var id = Guid.NewGuid();
        var sessions = new[] { new ActiveStreamSnapshot(id, "Movie.mkv", "movie", 0, 1000) };

        // Index 9 is stale (config only has 2 providers) and must be dropped.
        Dictionary<int, NzbWebDAV.Database.Models.NzbProviderStats> Lookup(string key) => new()
        {
            [0] = new() { JobName = key, ProviderIndex = 0, TotalBytes = 10 },
            [9] = new() { JobName = key, ProviderIndex = 9, TotalBytes = 999 },
        };
        var hosts = new[] { "a", "b" };

        var dtos = StreamSessionRegistry.BuildDtos(sessions, Lookup, hosts);

        Assert.Single(dtos[0].Providers);
        Assert.Equal(0, dtos[0].Providers[0].ProviderIndex);
    }
}
