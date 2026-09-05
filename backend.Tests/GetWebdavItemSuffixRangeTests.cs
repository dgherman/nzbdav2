using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests;

/// <summary>
/// Regression coverage for GitHub issue #33: suffix byte ranges (bytes=-N) on the /view/
/// endpoint were parsed identically to bytes=N- (RangeStart=N, RangeEnd=null), causing
/// GetWebdavItemController to serve from byte N to EOF instead of the last N bytes.
/// </summary>
public class GetWebdavItemSuffixRangeTests
{
    private const string StaticDownloadKey = "test-static-download-key";

    private static HttpContext BuildContext(string? rangeHeader)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "webdav.static-download-key", ConfigValue = StaticDownloadKey }
        ]);

        var context = new DefaultHttpContext();
        context.Request.Path = "/view/movie.mkv";
        context.Request.QueryString = new QueryString($"?downloadKey={StaticDownloadKey}");
        context.Items["configManager"] = configManager;
        if (rangeHeader is not null)
            context.Request.Headers["Range"] = rangeHeader;

        return context;
    }

    [Fact]
    public void SuffixRange_ParsesAsSuffixLength_NotAsRangeStart()
    {
        var request = new GetWebdavItemRequest(BuildContext("bytes=-1048576"));

        Assert.Null(request.RangeStart);
        Assert.Null(request.RangeEnd);
        Assert.Equal(1048576, request.SuffixLength);
    }

    [Fact]
    public void ClosedRange_ParsesStartAndEnd()
    {
        var request = new GetWebdavItemRequest(BuildContext("bytes=100-200"));

        Assert.Equal(100, request.RangeStart);
        Assert.Equal(200, request.RangeEnd);
        Assert.Null(request.SuffixLength);
    }

    [Fact]
    public void OpenEndedRange_ParsesStartOnly()
    {
        var request = new GetWebdavItemRequest(BuildContext("bytes=100-"));

        Assert.Equal(100, request.RangeStart);
        Assert.Null(request.RangeEnd);
        Assert.Null(request.SuffixLength);
    }

    [Fact]
    public void NoRangeHeader_LeavesAllRangeFieldsNull()
    {
        var request = new GetWebdavItemRequest(BuildContext(null));

        Assert.Null(request.RangeStart);
        Assert.Null(request.RangeEnd);
        Assert.Null(request.SuffixLength);
    }

    [Fact]
    public void ResolveByteRange_SuffixRange_ReturnsLastNBytesOfFile()
    {
        var request = new GetWebdavItemRequest(BuildContext("bytes=-1048576"));
        const long fileSize = 4_194_304; // 4 MiB

        var resolved = ListWebdavDirectoryController.ResolveByteRange(request, fileSize);

        Assert.NotNull(resolved);
        Assert.Equal(3_145_728, resolved!.Value.Start); // 4MiB - 1MiB
        Assert.Equal(4_194_303, resolved.Value.End);
        Assert.Equal(1_048_576, resolved.Value.ChunkSize);
    }

    [Fact]
    public void ResolveByteRange_SuffixLargerThanFile_ClampsToWholeFileWithoutNegativeStart()
    {
        var request = new GetWebdavItemRequest(BuildContext("bytes=-999999999"));
        const long fileSize = 4_194_304; // 4 MiB, far smaller than the suffix requested

        var resolved = ListWebdavDirectoryController.ResolveByteRange(request, fileSize);

        Assert.NotNull(resolved);
        Assert.Equal(0, resolved!.Value.Start);
        Assert.Equal(fileSize - 1, resolved.Value.End);
        Assert.Equal(fileSize, resolved.Value.ChunkSize);
    }

    [Fact]
    public void ResolveByteRange_ClosedRange_IsUnaffectedBySuffixHandling()
    {
        var request = new GetWebdavItemRequest(BuildContext("bytes=100-199"));
        const long fileSize = 4_194_304;

        var resolved = ListWebdavDirectoryController.ResolveByteRange(request, fileSize);

        Assert.NotNull(resolved);
        Assert.Equal(100, resolved!.Value.Start);
        Assert.Equal(199, resolved.Value.End);
        Assert.Equal(100, resolved.Value.ChunkSize);
    }

    [Fact]
    public void ResolveByteRange_OpenEndedRange_GoesToEndOfFile()
    {
        var request = new GetWebdavItemRequest(BuildContext("bytes=4000000-"));
        const long fileSize = 4_194_304;

        var resolved = ListWebdavDirectoryController.ResolveByteRange(request, fileSize);

        Assert.NotNull(resolved);
        Assert.Equal(4_000_000, resolved!.Value.Start);
        Assert.Equal(fileSize - 1, resolved.Value.End);
        Assert.Equal(fileSize - 4_000_000, resolved.Value.ChunkSize);
    }

    [Fact]
    public void ResolveByteRange_NoRange_ReturnsNull()
    {
        var request = new GetWebdavItemRequest(BuildContext(null));
        const long fileSize = 4_194_304;

        var resolved = ListWebdavDirectoryController.ResolveByteRange(request, fileSize);

        Assert.Null(resolved);
    }
}
