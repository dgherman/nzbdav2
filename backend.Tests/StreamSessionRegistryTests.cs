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
}
