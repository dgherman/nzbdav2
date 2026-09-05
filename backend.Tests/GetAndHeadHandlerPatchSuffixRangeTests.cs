using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Tests;

/// <summary>
/// Regression coverage for GitHub issue #33 (WebDAV path): NWebDav's request.GetRange() parses
/// a suffix range (bytes=-N) as Start=null/End=N ("the last N bytes" per RFC 9110 14.1.2), but
/// GetAndHeadHandlerPatch treated End as an absolute offset, serving the FIRST N bytes instead
/// of the LAST N bytes.
/// </summary>
public class GetAndHeadHandlerPatchSuffixRangeTests
{
    private const int FileSize = 100;

    // byte[i] == i, so the tail (or any window) of the file is trivially verifiable by value.
    private static byte[] BuildPayload() => Enumerable.Range(0, FileSize).Select(i => (byte)i).ToArray();

    private static (HttpContext Context, GetAndHeadHandlerPatch Handler) BuildRequest(string? rangeHeader)
    {
        var item = new FakeStoreItem(BuildPayload());
        var store = new FakeStore(item);
        var handler = new GetAndHeadHandlerPatch(store);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = "/movie.mkv";
        context.Response.Body = new MemoryStream();
        if (rangeHeader is not null)
            context.Request.Headers["Range"] = rangeHeader;

        return (context, handler);
    }

    [Fact]
    public async Task SuffixRange_ServesLastNBytes_NotFirstNBytes()
    {
        var (context, handler) = BuildRequest("bytes=-10");

        await handler.HandleRequestAsync(context);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal($"bytes 90-99/{FileSize}", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(10, context.Response.ContentLength);

        var body = ((MemoryStream)context.Response.Body).ToArray();
        Assert.Equal(10, body.Length);
        Assert.Equal(Enumerable.Range(90, 10).Select(i => (byte)i), body);
    }

    [Fact]
    public async Task SuffixRangeLargerThanFile_ClampsToWholeFileWithoutNegativeStart()
    {
        var (context, handler) = BuildRequest("bytes=-999999");

        await handler.HandleRequestAsync(context);

        Assert.Equal($"bytes 0-{FileSize - 1}/{FileSize}", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(FileSize, context.Response.ContentLength);

        var body = ((MemoryStream)context.Response.Body).ToArray();
        Assert.Equal(BuildPayload(), body);
    }

    [Fact]
    public async Task ClosedRange_IsUnaffectedBySuffixHandling()
    {
        var (context, handler) = BuildRequest("bytes=10-19");

        await handler.HandleRequestAsync(context);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 10-19/100", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(10, context.Response.ContentLength);

        var body = ((MemoryStream)context.Response.Body).ToArray();
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (byte)i), body);
    }

    [Fact]
    public async Task OpenEndedRange_ServesFromStartToEndOfFile()
    {
        var (context, handler) = BuildRequest("bytes=50-");

        await handler.HandleRequestAsync(context);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal($"bytes 50-{FileSize - 1}/{FileSize}", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(50, context.Response.ContentLength);

        var body = ((MemoryStream)context.Response.Body).ToArray();
        Assert.Equal(Enumerable.Range(50, 50).Select(i => (byte)i), body);
    }

    [Fact]
    public async Task NoRangeHeader_ServesWholeFile()
    {
        var (context, handler) = BuildRequest(null);

        await handler.HandleRequestAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(FileSize, context.Response.ContentLength);

        var body = ((MemoryStream)context.Response.Body).ToArray();
        Assert.Equal(BuildPayload(), body);
    }

    private sealed class FakeStoreItem(byte[] payload) : BaseStoreItem
    {
        public override string Name => "movie.mkv";
        public override string UniqueKey => "fake-movie";
        public override long FileSize => payload.Length;
        public override DateTime CreatedAt => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream(payload, writable: false));

        protected override Task<DavStatusCode> UploadFromStreamAsync(UploadFromStreamRequest request)
            => throw new NotSupportedException();

        protected override Task<StoreItemResult> CopyAsync(CopyRequest request)
            => throw new NotSupportedException();
    }

    private sealed class FakeStore(IStoreItem item) : IStore
    {
        public Task<IStoreItem?> GetItemAsync(Uri uri, CancellationToken cancellationToken)
            => Task.FromResult<IStoreItem?>(item);

        public Task<IStoreCollection?> GetCollectionAsync(Uri uri, CancellationToken cancellationToken)
            => Task.FromResult<IStoreCollection?>(null);
    }
}
