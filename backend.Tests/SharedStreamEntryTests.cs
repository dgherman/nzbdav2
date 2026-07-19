using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Streams;
using Xunit;

namespace NzbWebDAV.Tests;

public class SharedStreamEntryTests
{
    private static SharedStreamEntry CreateEntry(
        Stream inner,
        out SemaphoreSlim slot,
        int gracePeriodSeconds = 0,
        long streamLength = 4096,
        int ringBufferSize = 64 * 1024)
    {
        slot = new SemaphoreSlim(1, 1);
        Assert.True(slot.Wait(0)); // the entry owns the slot and releases it during cleanup
        return new SharedStreamEntry(
            inner,
            slot,
            Guid.NewGuid(),
            basePosition: 0,
            streamLength: streamLength,
            ringBufferSize: ringBufferSize,
            gracePeriodSeconds: gracePeriodSeconds,
            evictCallback: (_, _) => { },
            entryCts: new CancellationTokenSource());
    }

    private static Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout) =>
        TestAsync.WaitUntil(condition, timeout);

    [Fact]
    public async Task Pump_ParkedOnGate_ExitsWhenEntryIsEvicted()
    {
        // Regression: the pump parked on an untokened gate wait and cleanup disposed the gate under it.
        // ManualResetEventSlim.Dispose does not release waiters, so the pump thread leaked on every
        // ordinary teardown. Grace is 1s to guarantee the pump reaches the gate before cleanup runs.
        var inner = new FakeInnerStream(payloadBytes: 1024, gateFirstRead: true);
        var entry = CreateEntry(inner, out var slot, gracePeriodSeconds: 1);
        var handleId = entry.RegisterReader(0);
        entry.StartPump();

        await inner.FirstReadEntered.WaitAsync(TimeSpan.FromSeconds(5));
        entry.DetachReader(handleId); // last reader leaves -> gate resets, grace timer armed
        inner.ReleaseFirstRead();     // pump finishes this cycle, loops, and parks on the closed gate

        Assert.True(
            await WaitUntil(() => entry.PumpTask is { IsCompleted: true }, TimeSpan.FromSeconds(10)),
            "pump did not exit after the entry was evicted — the parked-pump thread leak is back");
        Assert.Equal(1, slot.CurrentCount); // cleanup released the concurrent-stream slot
    }

    [Fact]
    public async Task CaptureDataSignal_AfterEof_IsAlreadyCompleted()
    {
        // Regression (lost wakeup): the EOF path used to swap in a fresh TCS and signal only the old
        // one. A reader that captured the fresh TCS awaited a task nobody would ever complete and hung
        // until its request token fired. Terminal states must leave the signal permanently completed.
        var inner = new FakeInnerStream(payloadBytes: 0); // first read returns 0 -> immediate EOF
        var entry = CreateEntry(inner, out _);
        entry.StartPump();

        Assert.True(await WaitUntil(() => entry.IsCompleted, TimeSpan.FromSeconds(5)), "pump never reached EOF");

        var signal = entry.CaptureDataSignal();
        Assert.True(signal.IsCompleted, "signal captured after EOF must already be completed, or readers hang");
    }

    [Fact]
    public async Task Reader_AtWriteFrontier_ReturnsZeroPromptly_WhenPumpReachesEof()
    {
        var inner = new FakeInnerStream(payloadBytes: 0);
        var entry = CreateEntry(inner, out _);
        var handle = entry.TryAttachReader(0);
        Assert.NotNull(handle);
        entry.StartPump();

        var read = handle!.ReadAsync(new byte[256], 0, 256);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(read, finished); // no hang
        Assert.Equal(0, await read);
    }

    [Fact]
    public async Task Reader_ObservesFailure_AsIoException()
    {
        var inner = new FakeInnerStream(throwOnRead: new InvalidDataException("boom"));
        var entry = CreateEntry(inner, out _);
        var handle = entry.TryAttachReader(0);
        Assert.NotNull(handle);
        entry.StartPump();

        var read = handle!.ReadAsync(new byte[256], 0, 256);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(read, finished); // no hang
        await Assert.ThrowsAsync<IOException>(() => read);
    }

    [Fact]
    public async Task Cleanup_RunsOnce_EvenWhenAFailedEntryIsAlsoDisposed()
    {
        // TransitionToFailed cleans up but leaves the entry in Failed (not Disposed), so a subsequent
        // Dispose() used to clean up a second time and release the concurrent-stream slot twice.
        var inner = new FakeInnerStream(throwOnRead: new InvalidDataException("boom"));
        var entry = CreateEntry(inner, out var slot);
        entry.StartPump();

        Assert.True(
            await WaitUntil(() => entry.State == SharedStreamEntry.EntryState.Failed, TimeSpan.FromSeconds(5)),
            "entry never transitioned to Failed");
        Assert.Equal(1, slot.CurrentCount); // released by the failure cleanup

        var ex = Record.Exception(() => entry.Dispose());

        Assert.Null(ex); // pre-guard: SemaphoreFullException from the second release
        Assert.Equal(1, slot.CurrentCount); // still exactly one, not two
    }

    [Fact]
    public async Task Pump_PausedOnBackpressure_KeepsInnerStreamAlive_WhileReadersAreAttached()
    {
        // Regression: a paused player stops consuming -> ring fills -> pump stops reading the inner
        // stream -> the inner stream's idle watchdog (which only counts reads) self-cancelled its
        // workers after 60s, so resuming paid a full cold rebuild. A pump waiting on backpressure with
        // readers attached is not orphaned and must say so.
        var inner = new TouchCountingStream(chunkSize: 4096);
        var entry = CreateEntry(inner, out _, gracePeriodSeconds: 60, streamLength: long.MaxValue, ringBufferSize: 8192);

        var handle = entry.TryAttachReader(0); // attached, but never reads: the ring fills and stays full
        Assert.NotNull(handle);
        entry.StartPump();

        Assert.True(
            await WaitUntil(() => inner.TouchCount > 0, TimeSpan.FromSeconds(5)),
            "pump never touched the inner stream while paused on backpressure — resume would cold-rebuild");

        entry.Dispose();
    }

    [Fact]
    public async Task Reader_Cancellation_Throws_RatherThanLookingLikeEof()
    {
        // Returning 0 here would read as a premature EOF to NzbFileStream, which would then rebuild a
        // replacement stream (burning connections) for a client that has already disconnected.
        var inner = new FakeInnerStream(payloadBytes: 0, gateFirstRead: true); // never publishes data
        var entry = CreateEntry(inner, out _);
        var handle = entry.TryAttachReader(0);
        Assert.NotNull(handle);
        entry.StartPump();

        using var cts = new CancellationTokenSource();
        var read = handle!.ReadAsync(new byte[256], 0, 256, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
