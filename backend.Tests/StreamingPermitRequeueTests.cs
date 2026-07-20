using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using Usenet.Nzb;
using UsenetSharp.Models;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// StreamingConnectionLimiter's constructor claims a process-wide static instance, and
/// BufferedSegmentStream reads it directly. Any test that installs one changes the behaviour of
/// every other test that builds a BufferedSegmentStream, so those classes must not run in parallel.
/// </summary>
[CollectionDefinition(BufferedStreamCollection.Name, DisableParallelization = true)]
public class BufferedStreamCollection
{
    public const string Name = "BufferedSegmentStream";
}

[Collection(BufferedStreamCollection.Name)]
public class StreamingPermitRequeueTests
{
    private const int SegmentSize = 1024;
    private const int SegmentCount = 6;

    /// <summary>
    /// Serves a payload unique to each segment index, so the assertion distinguishes "the segment
    /// was actually fetched" from "the segment was zero-filled or served out of order".
    /// </summary>
    private sealed class PerSegmentNntpClient : INntpClient
    {
        private int _served;
        public int Served => Volatile.Read(ref _served);

        public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
        {
            Interlocked.Increment(ref _served);
            var index = int.Parse(segmentId.Split('-')[1].Split('@')[0]);
            var header = new UsenetYencHeader
            {
                FileName = "test.mkv",
                FileSize = (long)SegmentSize * SegmentCount,
                LineLength = 128,
                PartNumber = index + 1,
                TotalParts = SegmentCount,
                PartSize = SegmentSize,
                PartOffset = (long)SegmentSize * index,
            };
            var payload = new byte[SegmentSize];
            Array.Fill(payload, PayloadByte(index));
            return Task.FromResult(new YencHeaderStream(header, null, new MemoryStream(payload)));
        }

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UsenetDateResponse> DateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WaitForReady(CancellationToken ct) => Task.CompletedTask;
        public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static byte PayloadByte(int index) => (byte)(index + 1);

    [Fact]
    public async Task PermitTimeout_RequeuesTheSegment_InsteadOfStrandingTheStream()
    {
        // Regression for the 2026-07-16 spec, Finding 3. The worker had already popped the job from
        // the queue when it timed out waiting for a global streaming permit, and then did a bare
        // `continue`. Nothing re-queued it: the ordering task blocked at that index forever, the
        // straggler monitor could not help (the assignment is removed in the finally), and the only
        // recovery was the 60s idle watchdog tearing the stream down into a premature-EOF rebuild.
        // Under permit exhaustion — precisely when several streams are competing — click-to-play
        // degraded into 60s+ stall loops.
        //
        // The shape here: one permit in the whole pool, and the test holds it. Every worker times
        // out, repeatedly, while the reader waits. Once the permit is returned the stream must go on
        // to deliver every segment intact. Without the re-queue this read never completes.
        var config = new ConfigManager();
        config.UpdateValues(new List<ConfigItem>
        {
            new() { ConfigName = "usenet.total-streaming-connections", ConfigValue = "1" },
        });

        var previousLimiter = StreamingConnectionLimiter.Instance;
        var previousTimeout = BufferedSegmentStream.PermitAcquireTimeout;
        using var limiter = new StreamingConnectionLimiter(config);

        // Short enough that the workers cycle the timeout several times while the permit is held,
        // instead of the test sitting through the production 60s wait.
        BufferedSegmentStream.PermitAcquireTimeout = TimeSpan.FromMilliseconds(60);

        try
        {
            // Take the only permit. Workers cannot fetch anything until this lease is disposed.
            var heldLease = await limiter.AcquireLeaseAsync(TimeSpan.FromSeconds(5), CancellationToken.None, "test-hold");
            Assert.NotNull(heldLease);
            Assert.Equal(0, limiter.AvailableConnections);

            var segmentIds = new string[SegmentCount];
            var segmentSizes = new long[SegmentCount];
            for (var i = 0; i < SegmentCount; i++)
            {
                segmentIds[i] = $"seg-{i}@test";
                segmentSizes[i] = SegmentSize;
            }

            var client = new PerSegmentNntpClient();
            var context = new ConnectionUsageContext(ConnectionUsageType.BufferedStreaming, new ConnectionUsageDetails { Text = "test" });

            await using var stream = new BufferedSegmentStream(
                segmentIds,
                fileSize: (long)SegmentSize * SegmentCount,
                client,
                concurrentConnections: 2,
                bufferSegmentCount: 4,
                cancellationToken: CancellationToken.None,
                usageContext: context,
                segmentSizes: segmentSizes);

            // Start reading while the permit is unavailable, so the first jobs are popped and hit
            // the timeout path rather than queueing behind an acquire that later succeeds.
            var buffer = new byte[SegmentSize * SegmentCount];
            var readTask = ReadFully(stream, buffer);

            // Long enough for several timeout/re-queue cycles at a 60ms permit timeout. If the job
            // were dropped, the ordering task is already wedged by the time the permit comes back.
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            Assert.Equal(0, client.Served); // nothing could have been fetched without a permit

            heldLease!.Dispose();

            // 30s: comfortably more than this 6KB file needs, and deliberately less than the 60s
            // idle watchdog, so a regression fails here rather than being papered over by the
            // watchdog teardown and rebuild.
            var read = await readTask.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(buffer.Length, read);
            for (var i = 0; i < SegmentCount; i++)
            {
                for (var offset = 0; offset < SegmentSize; offset++)
                {
                    // Byte-exact: a zero-filled (gracefully degraded) segment would read 0 here, and a
                    // re-queue that lost ordering would read another segment's fill byte.
                    Assert.Equal(PayloadByte(i), buffer[i * SegmentSize + offset]);
                }
            }

            // Every segment fetched at least once, and the retry cycling did not multiply fetches.
            Assert.InRange(client.Served, SegmentCount, SegmentCount * 2);

            // The timeouts really happened — otherwise this test passes without exercising the path.
            var stats = limiter.GetStats();
            Assert.True(stats.Timeouts > 0, $"expected permit timeouts, got {stats.Timeouts}");
        }
        finally
        {
            BufferedSegmentStream.PermitAcquireTimeout = previousTimeout;
            StreamingConnectionLimiter.SetInstanceForTests(previousLimiter);
        }
    }

    private static async Task<int> ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
