using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using Serilog;

namespace NzbWebDAV.Api.Controllers.GcDiagnostics;

/// <summary>
/// Forces a full collection and reports the generation sizes on either side of it.
///
/// This exists because `last_collection_heap_size` — the only trustworthy heap metric, since
/// GC.GetTotalMemory and dotnet_total_memory_bytes both count uncollected garbage — is a snapshot
/// taken *at the last collection*. When the process goes idle the collections stop, the metric
/// freezes at whatever the busy state was, and the heap becomes unreadable exactly when you want to
/// read its floor. Measured on production 2026-07-17: 90 seconds after playback stopped, gen2 was
/// still pinned at 13 and LOH still reported its mid-playback 2.71 GB.
///
/// The before/after pair is the point. "LOH is 2.7 GB" answers nothing on its own; "LOH was 2.7 GB
/// and a forced compacting collection took it to X" separates rooted from collectable, which is the
/// distinction that says whether a number is a leak or just garbage waiting for pressure.
///
/// Off unless NZBDAV_GC_DIAG=1. A blocking compacting gen2 stops every thread for as long as it
/// takes — on a 4 GiB heap that is seconds, and any stream in flight will feel it. This is a
/// diagnostic to reach for deliberately, not something to leave enabled or to poll.
/// </summary>
[ApiController]
[Route("api/gc-diagnostics")]
public class GcDiagnosticsController : BaseApiController
{
    private sealed record GenerationSnapshot(
        string Generation,
        long SizeBytes,
        long FragmentationBytes);

    private sealed record HeapSnapshot(
        long TotalHeapBytes,
        long CommittedBytes,
        long HeapLimitBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        List<GenerationSnapshot> Generations);

    private static readonly string[] GenerationNames = ["gen0", "gen1", "gen2", "loh", "poh"];

    private static HeapSnapshot Snapshot()
    {
        var info = GC.GetGCMemoryInfo();
        var generations = new List<GenerationSnapshot>();

        for (var i = 0; i < info.GenerationInfo.Length && i < GenerationNames.Length; i++)
        {
            var gen = info.GenerationInfo[i];
            generations.Add(new GenerationSnapshot(
                GenerationNames[i],
                gen.SizeAfterBytes,
                gen.FragmentationAfterBytes));
        }

        return new HeapSnapshot(
            info.HeapSizeBytes,
            info.TotalCommittedBytes,
            info.TotalAvailableMemoryBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            generations);
    }

    protected override Task<IActionResult> HandleRequest()
    {
        if (Environment.GetEnvironmentVariable("NZBDAV_GC_DIAG") != "1")
        {
            return Task.FromResult<IActionResult>(NotFound(new BaseApiResponse
            {
                Status = false,
                Error = "GC diagnostics are disabled. Set NZBDAV_GC_DIAG=1 to enable. " +
                        "This endpoint forces a blocking compacting gen2 collection, which stalls all threads."
            }));
        }

        var before = Snapshot();

        // Two passes with a finalizer drain between them: the first collection runs finalizers for
        // unreachable objects, and anything they release only becomes collectable on the pass after.
        // Without the second pass a buffer held by a finalizable stream reads as still-live, which
        // is the exact false positive this endpoint exists to rule out.
        var stopwatch = Stopwatch.StartNew();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        stopwatch.Stop();

        var after = Snapshot();

        var reclaimed = before.TotalHeapBytes - after.TotalHeapBytes;
        Log.Warning("[GcDiagnostics] Forced collection took {ElapsedMs}ms. Heap {BeforeMB}MB -> {AfterMB}MB (reclaimed {ReclaimedMB}MB). " +
                    "Whatever remains is rooted, not garbage.",
            stopwatch.ElapsedMilliseconds,
            before.TotalHeapBytes / (1024 * 1024),
            after.TotalHeapBytes / (1024 * 1024),
            reclaimed / (1024 * 1024));

        return Task.FromResult<IActionResult>(Ok(new
        {
            status = true,
            forcedCollectionMs = stopwatch.ElapsedMilliseconds,
            reclaimedBytes = reclaimed,
            // After a forced collection this is live+retained, with no garbage left to hide behind.
            // A number that stays high here is rooted by something and is worth chasing; a number
            // that collapses was never a leak.
            before,
            after,
            note = "Sizes are post-collection (SizeAfterBytes). 'after' is the honest floor: " +
                   "a forced compacting gen2 has just run, so anything still counted is reachable."
        }));
    }
}
