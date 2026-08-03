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
/// Reproduction harness for the churn workload reported after v0.11.12: a player that opens a file,
/// seeks a few times and closes again, several times in a row. BufferedSegmentStream is not
/// seekable (<c>CanSeek => false</c>), so every seek is a fresh HTTP range request that builds a
/// whole new stream and abandons the previous one mid-flight — a seek IS an open/abandon cycle.
///
/// The scarce global resources on that path are the concurrent-stream slots and the streaming
/// permits. If either leaks per abandoned stream, playback does not fail outright: it silently
/// degrades to the private/unbuffered fallback, which is what "I had to start it several times
/// before it worked" looks like from the outside. These tests assert both pools return to full
/// after repeated mid-flight abandonment.
/// </summary>
[Collection(BufferedStreamCollection.Name)]
public class StreamChurnTests
{
    private const int SegmentSize = 1024;
    private const int SegmentCount = 64;
    private const int SlotBudget = 4;
    private const int PermitBudget = 4;

    /// <summary>
    /// Serves segments with a small delay, so a stream that is disposed after one read still has
    /// workers in flight holding permits — the state an abandoned seek leaves behind.
    /// </summary>
    private sealed class SlowSegmentNntpClient : INntpClient
    {
        private readonly TimeSpan _delay;
        public SlowSegmentNntpClient(TimeSpan delay) => _delay = delay;

        public async Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
        {
            await Task.Delay(_delay, ct).ConfigureAwait(false);
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
            Array.Fill(payload, (byte)(index + 1));
            return new YencHeaderStream(header, null, new MemoryStream(payload));
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

    /// <summary>
    /// How many concurrent-stream slots are currently free. Takes them and immediately gives them
    /// back, which is safe because this collection does not run in parallel.
    /// </summary>
    private static int ProbeAvailableSlots(int budget)
    {
        var taken = new List<SemaphoreSlim>();
        for (var i = 0; i < budget; i++)
        {
            var slot = BufferedSegmentStream.TryAcquireSlot();
            if (slot == null) break;
            taken.Add(slot);
        }
        foreach (var slot in taken) slot.Release();
        return taken.Count;
    }

    private static (string[] ids, long[] sizes) BuildSegments(int count)
    {
        var ids = new string[count];
        var sizes = new long[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = $"seg-{i}@test";
            sizes[i] = SegmentSize;
        }
        return (ids, sizes);
    }

    [Fact]
    public async Task RapidOpenAndAbandon_ReturnsEveryStreamSlotAndPermit()
    {
        // 20 cycles of "start playing, then seek away after the first block". Each cycle takes a
        // concurrent-stream slot, reads one segment, and disposes the stream while its prefetch
        // workers are still fetching. Cycle N+1 can only get a slot if cycle N gave one back, so a
        // per-abandonment leak fails on the first cycle past the budget rather than at the end.
        const int cycles = 20;

        var config = new ConfigManager();
        config.UpdateValues(new List<ConfigItem>
        {
            new() { ConfigName = "usenet.total-streaming-connections", ConfigValue = PermitBudget.ToString() },
        });

        var previousLimiter = StreamingConnectionLimiter.Instance;
        using var limiter = new StreamingConnectionLimiter(config);
        BufferedSegmentStream.SetMaxConcurrentStreams(SlotBudget);

        try
        {
            var (segmentIds, segmentSizes) = BuildSegments(SegmentCount);
            var client = new SlowSegmentNntpClient(TimeSpan.FromMilliseconds(10));
            var context = new ConnectionUsageContext(
                ConnectionUsageType.BufferedStreaming, new ConnectionUsageDetails { Text = "churn-test" });

            for (var cycle = 0; cycle < cycles; cycle++)
            {
                var slot = BufferedSegmentStream.TryAcquireSlot();
                Assert.True(slot != null,
                    $"no concurrent-stream slot on cycle {cycle} of {cycles} with a budget of {SlotBudget} — " +
                    "abandoned streams are not releasing their slot");

                var stream = new BufferedSegmentStream(
                    segmentIds,
                    fileSize: (long)SegmentSize * SegmentCount,
                    client,
                    concurrentConnections: 3,
                    bufferSegmentCount: 8,
                    cancellationToken: CancellationToken.None,
                    usageContext: context,
                    segmentSizes: segmentSizes);
                stream.SetAcquiredSlot(slot!);

                // One segment only: the workers are mid-flight when the stream goes away, which is
                // what a seek does to the stream it replaces.
                var buffer = new byte[SegmentSize];
                var read = await stream.ReadAsync(buffer, 0, buffer.Length)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(SegmentSize, read);
                Assert.Equal((byte)1, buffer[0]); // first segment's fill byte, not a zero-filled read

                await stream.DisposeAsync();
            }

            // Permits unwind on the worker tasks, so they come back slightly after disposal returns.
            Assert.True(
                await TestAsync.WaitUntil(() => limiter.AvailableConnections == PermitBudget, TimeSpan.FromSeconds(30)),
                $"streaming permits leaked across {cycles} abandoned streams: " +
                $"{limiter.AvailableConnections} of {PermitBudget} available");

            // And the full slot budget is obtainable again, not just one slot per cycle.
            var recovered = new List<SemaphoreSlim>();
            for (var i = 0; i < SlotBudget; i++)
            {
                var slot = BufferedSegmentStream.TryAcquireSlot();
                if (slot != null) recovered.Add(slot);
            }
            var recoveredCount = recovered.Count;
            foreach (var slot in recovered) slot.Release();

            Assert.True(recoveredCount == SlotBudget,
                $"only {recoveredCount} of {SlotBudget} concurrent-stream slots came back after {cycles} cycles");
        }
        finally
        {
            // Back to the assembly-wide budget the shared-stream tests assume.
            BufferedSegmentStream.SetMaxConcurrentStreams(32);
            StreamingConnectionLimiter.SetInstanceForTests(previousLimiter);
        }
    }

    [Fact]
    public async Task SeekChurn_AcrossSharedStreamRegions_ReclaimsEntriesAndSlots()
    {
        // The same churn against the shared-stream path: open the file at byte 0, "seek" to a far
        // offset (a second region entry), close both, repeat. Every entry holds a concurrent-stream
        // slot until its teardown completes, so an entry that is not reclaimed permanently consumes
        // one — and once the slots are gone, later playbacks silently drop to private streams.
        const int cycles = 12;
        const long streamLength = 100_000_000;
        const int ringBufferSize = 64 * 1024;

        var davItemId = Guid.NewGuid();
        var created = 0;
        var innerStreams = new List<FakeInnerStream>();

        BufferedSegmentStream.SetMaxConcurrentStreams(SlotBudget);

        try
        {
            SharedStreamManager.SharedStreamFactoryResult Factory(CancellationToken ct)
            {
                Interlocked.Increment(ref created);
                var inner = new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true);
                lock (innerStreams) innerStreams.Add(inner);
                return new SharedStreamManager.SharedStreamFactoryResult(inner);
            }

            for (var cycle = 0; cycle < cycles; cycle++)
            {
                var front = SharedStreamManager.GetOrCreate(
                    davItemId, startPosition: 0, streamLength, ringBufferSize, gracePeriodSeconds: 0, Factory);
                Assert.True(front != null,
                    $"no shared stream on cycle {cycle} of {cycles} with a slot budget of {SlotBudget} — " +
                    "entries from earlier cycles are still holding their slots");

                // The seek: far past the front pump, so it needs its own region rather than attaching.
                var seekTarget = 90_000_000;
                var tail = SharedStreamManager.TryAttach(davItemId, seekTarget)
                           ?? SharedStreamManager.GetOrCreate(
                               davItemId, seekTarget, streamLength, ringBufferSize, gracePeriodSeconds: 0, Factory);
                Assert.True(tail != null, $"no shared stream for the seek on cycle {cycle} of {cycles}");

                tail!.Dispose();
                front!.Dispose();
                SharedStreamManager.Evict(davItemId);

                Assert.True(
                    await TestAsync.WaitUntil(
                        () => SharedStreamManager.CountEntries(davItemId) == 0, TimeSpan.FromSeconds(10)),
                    $"entries were not reclaimed on cycle {cycle}: {SharedStreamManager.CountEntries(davItemId)} left");

                // Eviction unregisters the entry before its teardown finishes, so the slot comes back
                // a moment later. This is the resource the next playback actually competes for.
                Assert.True(
                    await TestAsync.WaitUntil(
                        () => ProbeAvailableSlots(SlotBudget) == SlotBudget, TimeSpan.FromSeconds(15)),
                    $"only {ProbeAvailableSlots(SlotBudget)} of {SlotBudget} concurrent-stream slots came back " +
                    $"after cycle {cycle} — an evicted entry is holding one");
            }

            // Every stream the factory built was torn down — nothing is still pumping in the
            // background against a file no reader has open.
            List<FakeInnerStream> snapshot;
            lock (innerStreams) snapshot = new List<FakeInnerStream>(innerStreams);
            Assert.True(
                await TestAsync.WaitUntil(() => snapshot.TrueForAll(x => x.IsDisposed), TimeSpan.FromSeconds(15)),
                $"{snapshot.FindAll(x => !x.IsDisposed).Count} of {created} inner streams were never disposed");

            // Slots all back: the next playback still gets the shared-stream path.
            var recovered = new List<SemaphoreSlim>();
            for (var i = 0; i < SlotBudget; i++)
            {
                var slot = BufferedSegmentStream.TryAcquireSlot();
                if (slot != null) recovered.Add(slot);
            }
            var recoveredCount = recovered.Count;
            foreach (var slot in recovered) slot.Release();

            Assert.True(recoveredCount == SlotBudget,
                $"only {recoveredCount} of {SlotBudget} concurrent-stream slots came back after {cycles} open/seek/close cycles");
        }
        finally
        {
            SharedStreamManager.Evict(davItemId);
            BufferedSegmentStream.SetMaxConcurrentStreams(32);
        }
    }

    [Fact]
    public async Task RapidFireSeeks_OutrunTeardown_ButEveryResourceComesBack()
    {
        // The previous test gives each cycle time to finish tearing down. A user dragging the seek
        // bar does not: the next request arrives while the abandoned entry is still being cleaned up,
        // so its slot has not been returned yet. GetOrCreate then legitimately returns null and the
        // caller falls back to a private stream — no crash, but a heavier path.
        //
        // What must NOT happen is that the resources stay gone. This pins the recovery: after the
        // burst, every entry is reclaimed and the whole slot budget is available again.
        const int cycles = 20;
        const long streamLength = 100_000_000;
        const int ringBufferSize = 64 * 1024;

        var davItemId = Guid.NewGuid();
        var innerStreams = new List<FakeInnerStream>();
        var fallbacks = 0;

        BufferedSegmentStream.SetMaxConcurrentStreams(SlotBudget);

        try
        {
            SharedStreamManager.SharedStreamFactoryResult Factory(CancellationToken ct)
            {
                var inner = new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true);
                lock (innerStreams) innerStreams.Add(inner);
                return new SharedStreamManager.SharedStreamFactoryResult(inner);
            }

            for (var cycle = 0; cycle < cycles; cycle++)
            {
                // No wait anywhere in this loop — each iteration steps on the previous teardown.
                var handle = SharedStreamManager.GetOrCreate(
                    davItemId, startPosition: cycle * 3_000_000L, streamLength, ringBufferSize,
                    gracePeriodSeconds: 0, Factory);

                if (handle == null)
                {
                    fallbacks++; // starved of a slot: production falls back to a private stream here
                    continue;
                }

                handle.Dispose();
                SharedStreamManager.Evict(davItemId);
            }

            Assert.True(fallbacks < cycles, "every single cycle was starved — the shared path never engaged at all");

            SharedStreamManager.Evict(davItemId);

            Assert.True(
                await TestAsync.WaitUntil(
                    () => SharedStreamManager.CountEntries(davItemId) == 0, TimeSpan.FromSeconds(15)),
                $"{SharedStreamManager.CountEntries(davItemId)} entries survived a {cycles}-cycle seek burst");

            List<FakeInnerStream> snapshot;
            lock (innerStreams) snapshot = new List<FakeInnerStream>(innerStreams);
            Assert.True(
                await TestAsync.WaitUntil(() => snapshot.TrueForAll(x => x.IsDisposed), TimeSpan.FromSeconds(15)),
                $"{snapshot.FindAll(x => !x.IsDisposed).Count} of {snapshot.Count} inner streams kept pumping " +
                "after the burst ended");

            Assert.True(
                await TestAsync.WaitUntil(
                    () => ProbeAvailableSlots(SlotBudget) == SlotBudget, TimeSpan.FromSeconds(15)),
                $"only {ProbeAvailableSlots(SlotBudget)} of {SlotBudget} concurrent-stream slots survived " +
                $"a {cycles}-cycle seek burst — the next playback is permanently degraded");
        }
        finally
        {
            SharedStreamManager.Evict(davItemId);
            BufferedSegmentStream.SetMaxConcurrentStreams(32);
        }
    }
}
