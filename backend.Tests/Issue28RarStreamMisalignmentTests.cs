using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using SharpCompress.Common.Rar.Headers;
using Usenet.Nzb;
using UsenetSharp.Models;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Regression coverage for issue #28's <c>RarUtil</c>/<c>NzbFileStream</c> seam: password-protected
/// (<c>-hp</c>) multi-volume RAR imports failed with "malformed vint", "Unknown Rar Header: N",
/// "arithmetic overflow" or "End of stream reached. Requested: 16 Read: 0".
///
/// Root cause was on the feed side, not in SharpCompress. <see cref="NzbWebDAV.Queue.FileProcessors.RarProcessor"/>
/// built the header-parsing stream through the old <c>UsenetStreamingClient.GetFastFileStream</c>,
/// which fabricates a <b>uniform</b> per-segment size table (<c>fileSize / segmentCount</c> for
/// every segment, remainder in the last). Real yEnc articles are a fixed larger size with a short
/// final segment, so the fabricated table's offsets drift hundreds of KB from reality across a
/// volume. RAR5 header parsing seeks past the stored data to the file/end-archive header near EOF;
/// <see cref="NzbFileStream"/> maps that file offset to the wrong segment/intra-offset through the
/// drifted table and SharpCompress then walks ciphertext or reads off the end.
///
/// The fix: RarProcessor no longer fabricates. It passes <c>segmentSizes: null</c>, so NzbFileStream
/// locates the target segment by interpolation-searching the real yEnc part headers.
///
/// These tests exercise the <c>NzbFileStream</c> feeding <see cref="RarUtil.GetRarHeadersAsync"/>
/// seam directly, via a fake <see cref="INntpClient"/> -- they pin the guard-rail exception typing
/// (including that a genuine wrong-password failure must NOT be relabeled as misalignment) at the
/// unit level. They do NOT drive <c>RarProcessor</c>'s own stream-selection code
/// (<c>GetFastNzbFileStream</c>) -- <see cref="Issue28RarProcessorIntegrationTests"/> does that,
/// against a real <c>UsenetStreamingClient</c> over a real (mock) NNTP server, and is the test that
/// actually fails if <c>RarProcessor</c> reverts to fabricating a uniform size table.
/// </summary>
public class Issue28RarStreamMisalignmentTests
{
    private const string Password = Issue28Fixtures.Password;
    private const long DeclaredUncompressedSize = Issue28Fixtures.DeclaredUncompressedSize;

    /// <summary>Serves a single volume's bytes sliced at an arbitrary (non-uniform) segment layout,
    /// with truthful yEnc part headers.</summary>
    private sealed class SlicingNntpClient(byte[] volume, long[] segmentSizes) : INntpClient
    {
        public string[] SegmentIds { get; } =
            Enumerable.Range(0, segmentSizes.Length).Select(i => $"seg-{i}").ToArray();

        private long OffsetOf(int index)
        {
            long o = 0;
            for (var i = 0; i < index; i++) o += segmentSizes[i];
            return o;
        }

        private UsenetYencHeader Header(int index) => new()
        {
            FileName = "hp.rar",
            FileSize = volume.Length,
            LineLength = 128,
            PartNumber = index + 1,
            TotalParts = segmentSizes.Length,
            PartSize = segmentSizes[index],
            PartOffset = OffsetOf(index),
        };

        private int IndexOf(string segmentId) => int.Parse(segmentId.Split('-')[1]);

        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct)
            => Task.FromResult(Header(IndexOf(segmentId)));

        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
        {
            var i = IndexOf(segmentId);
            var slice = new byte[segmentSizes[i]];
            Array.Copy(volume, OffsetOf(i), slice, 0, slice.Length);
            return Task.FromResult(new YencHeaderStream(Header(i), null, new MemoryStream(slice)));
        }

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static async Task<System.Collections.Generic.List<IRarHeader>> ParseHeadersAsync(
        byte[] volume, long[] realLayout, long[]? streamSegmentSizes, string? password = Password)
    {
        var client = new SlicingNntpClient(volume, realLayout);
        var ctx = new ConnectionUsageContext(ConnectionUsageType.QueueRarProcessing, "issue28-test");
        await using var stream = new NzbFileStream(
            client.SegmentIds, volume.Length, client, concurrentConnections: 3, usageContext: ctx,
            useBufferedStreaming: false, segmentSizes: streamSegmentSizes);
        return await RarUtil.GetRarHeadersAsync(stream, password, CancellationToken.None);
    }

    [Fact]
    public async Task Baseline_HeadersParseStraightFromDisk()
    {
        await using var fs = File.OpenRead(Path.Combine(Issue28Fixtures.FixtureDir(), "hp.part2.rar"));
        var headers = await RarUtil.GetRarHeadersAsync(fs, Password, CancellationToken.None);
        var file = Assert.Single(headers, h => h.HeaderType == HeaderType.File);
        Assert.Equal(DeclaredUncompressedSize, file.GetUncompressedSize());
    }

    /// <summary>
    /// The pre-fix path: RarProcessor fed the header parse a fabricated uniform size table. This
    /// test drives the same misaligned input and asserts the new guard rail turns the ~60s
    /// walk-to-EOF into an immediate, typed <see cref="RarStreamMisalignedException"/>.
    ///
    /// On pre-fix code (no guard rail) this test fails: the parse instead throws
    /// <c>InvalidFormatException</c> / <c>FormatException</c> after walking ciphertext.
    /// </summary>
    [Fact]
    public async Task FabricatedUniformSizeTable_IsRejectedAsMisaligned()
    {
        var volume = Issue28Fixtures.Volume("hp.part2.rar"); // 1,000,000-byte continuation volume
        var realLayout = Issue28Fixtures.NonUniformLayout(volume.Length, fullCount: 7, fullSize: 131_072);
        var fabricated = Issue28Fixtures.FabricateUniformTable(volume.Length, realLayout.Length);

        // Genuinely the pre-fix input: a uniform table that sums to the file size but whose offsets
        // do not match the real (non-uniform) layout.
        Assert.NotEqual(realLayout, fabricated);
        Assert.Equal(volume.Length, fabricated.Sum());

        await Assert.ThrowsAsync<RarStreamMisalignedException>(
            () => ParseHeadersAsync(volume, realLayout, fabricated));
    }

    /// <summary>
    /// The fix: RarProcessor passes <c>segmentSizes: null</c>, so NzbFileStream interpolation-searches
    /// the real yEnc part headers and every seek lands on the right byte. The <c>-hp</c> header parse
    /// then completes and reports the correct declared file size.
    ///
    /// On pre-fix code this scenario is what broke: fabricated uniform sizes made the post-data seek
    /// land in the wrong segment.
    /// </summary>
    [Fact]
    public async Task NoFabricatedTable_InterpolationSearchKeepsHeaderParseAligned()
    {
        var volume = Issue28Fixtures.Volume("hp.part2.rar");
        var realLayout = Issue28Fixtures.NonUniformLayout(volume.Length, fullCount: 7, fullSize: 131_072);

        var headers = await ParseHeadersAsync(volume, realLayout, streamSegmentSizes: null);

        var file = Assert.Single(headers, h => h.HeaderType == HeaderType.File);
        Assert.Equal(DeclaredUncompressedSize, file.GetUncompressedSize());
    }

    /// <summary>
    /// Cross-review requirement: a genuine wrong-password failure on an <c>-hp</c> archive must keep
    /// surfacing as a password/crypto error, never get relabeled as stream misalignment. RAR5's
    /// <c>Crypt</c> header is unencrypted metadata read successfully regardless of password
    /// correctness; the <c>Archive</c> header right after it is the first thing actually decrypted,
    /// and SharpCompress verifies the password there via a check value, throwing
    /// <see cref="SharpCompress.Common.CryptographicException"/> before any header content is
    /// trusted. The guard rail only relabels exceptions occurring AFTER a clean Archive header, so
    /// this must reach the caller unmodified.
    /// </summary>
    [Fact]
    public async Task WrongPassword_KeepsOriginalExceptionClass()
    {
        await using var fs = File.OpenRead(Path.Combine(Issue28Fixtures.FixtureDir(), "hp.part1.rar"));
        var ex = await Assert.ThrowsAsync<SharpCompress.Common.CryptographicException>(
            () => RarUtil.GetRarHeadersAsync(fs, "wrong-password", CancellationToken.None));
        Assert.IsNotType<RarStreamMisalignedException>(ex);
    }
}
