# Playback Stall and Freeze Fixes

This file is the durable starting point for the next investigation of video freezing, playback
stalling, truncated HTTP ranges, or long startup delays. It records the fixes already present in
this repository, the invariants they protect, and the production signatures that distinguish them.

## Scope and provenance

This ledger describes commits merged into this repository's `HEAD` (`dgherman/nzbdav2`). A commit
visible through `git log --all` is not necessarily part of this history: `fork-hoivikaj/*` is the
third-party `nzbdav/nzbdav` fork and `fork-fizzwhirl/*` is a downstream fork of this repository.
Confirm ancestry with `git merge-base --is-ancestor <commit> HEAD` before treating a change as
already adopted. Only `nzbdav-dev/nzbdav` is canonical upstream.

## First-response checklist

1. Record the title, episode, client, approximate wall-clock interval, and whether playback was
   Direct Play, remuxing, or transcoding.
2. Do not restart or modify the deployment before collecting evidence. A restart destroys useful
   stream, connection, and memory state.
3. Strip ANSI color sequences before searching Docker logs:

   ```bash
   docker logs nzbdav2 2>&1 \
     | sed 's/\x1b\[[0-9;]*m//g' \
     > /tmp/nzbdav2.log
   ```

4. Correlate HTTP ranges, job/file names, stream creation/disposal, RAR-volume boundaries, and
   provider failures over the same time interval.
5. Search first for these high-value signatures:

   ```bash
   grep -E 'Ordering stalled waiting for segment slot|Ordering task timed out|PERMANENT FAILURE|GRACEFUL DEGRADATION|PROVIDER CANCELLATION|timed out|STRAGGLER|premature|EOF|OutOfMemory|OOM|RUNAWAY' /tmp/nzbdav2.log
   ```

   In builds through v0.12.11, `Ordering task timed out` was ambiguous: it could mean an absent
   segment slot, but it also fired when the ordering writer was healthy and merely blocked on a full
   output channel. Correlate it with range creation time and shared-stream pumped bytes before
   assigning a cause. v0.12.12 reports `Ordering stalled waiting for segment slot` only after the
   writer is confirmed not to be waiting for its consumer.

6. Rule out container restart/OOM, NAS resource saturation, and a Plex/Jellyfin transcoder failure,
   but do not infer that the backend is healthy merely because audio continued. Audio and video can
   be buffered or requested independently, so a truncated video range can freeze while audio drains
   normally.
7. Preserve the exact logs and request timing used for attribution. Absence of a message from an old
   build does not prove an event did not happen; several historical failure paths were silent before
   their fixes.

## Streaming invariants

Future changes should preserve all of these:

### 1. Every dequeued segment retains an owner

A needed segment removed from a queue must end in exactly one of these states:

- successfully published into its ordered slot;
- deliberately replaced by a queued/racing attempt;
- converted to explicit graceful degradation after bounded retries; or
- causes the stream to terminate with an explicit error.

It must never disappear while its ordered slot remains null. A null slot makes the ordering task
wait and can ultimately truncate the HTTP response.

### 2. Cancellation meaning comes from token state

`OperationCanceledException` alone does not identify who requested cancellation:

| Token state | Meaning | Required action |
|---|---|---|
| Stream/parent token cancelled | Request, stream, seek, disposal, or shutdown ended the work | Stop; do not retry |
| Stream live, job CTS cancelled | Straggler monitor preempted this exact attempt | Monitor owns replacement publication; do not double-requeue |
| Stream and job tokens both live | Provider/lower-layer cancellation | Treat as provider failure and retry; defensively requeue if it escapes the retry loop |

This distinction is the basis of v0.12.11.

### 3. Retry publication and worker lifetime are coordinated

The straggler monitor cancels an attempt before publishing replacement work. Cancellation can resume
a worker inline, so workers must not interpret temporarily empty queues as completion while
`pendingRetryPublications` is non-zero. This is the invariant fixed by `55c7efe9`.

### 4. First successful publication wins

Urgent races may fetch the same segment concurrently, but only the first successful
`CompareExchange` may occupy the ordered slot. Losing results must be disposed. Urgent work must be
bounded and coalesced by segment index.

### 5. Resource ownership survives every exit path

Connection permits, global streaming leases, pooled segment buffers, connection locks, shared-stream
attachments, and cancellation sources must be released on success, failure, cancellation, race loss,
and early disposal. Permit acquisition timeout must requeue the already-dequeued segment before the
worker continues.

### 6. Range and multipart boundaries are byte-exact

Decoded yEnc offsets, HTTP range ends, suffix ranges, limited RAR-volume lengths, and combined-stream
part boundaries must agree. A boundary transition must neither skip nor repeat bytes. Ranges must not
prefetch beyond their effective end.

### 7. Pre-warming cannot own a live read

Next-volume pre-warming may reduce a natural RAR-boundary pause, but a real reader must have slot
priority and its own cancellation semantics. A coalesced live read must not inherit the pre-warm
caller's cancellation or slot wait.

### 8. Prefetch remains reader- and byte-bounded

Worker concurrency, the channel, ordered slots, shared readers, and next-volume warming all consume
memory. Prefetch must be bounded relative to the reader and constrained by the heap-derived byte
budget; it must not scale to the whole file.

## Historical fix ledger

The entries below are grouped by the failure mechanism they addressed. Dates and subjects can be
recovered with `git show -s --format=fuller <commit>`.

### Ordered delivery, worker stalls, and stragglers

| Commit | Change | Invariant protected |
|---|---|---|
| `83e74e6f` | Maintained segment order in the original buffered implementation | Ordered output |
| `2e044dc1` | Fixed buffered-stream stalls/deadlocks and exposed buffer sizing | Progress/diagnostics |
| `c2712e32`, `36d589b6`, `861719cb` | Hardened cancellation and bounded worker draining | Prompt teardown |
| `fc6a0094` | Fixed graceful-degradation loop behavior | Terminal ownership |
| `74c19a48` | Replaced ordering synchronization with lock-free per-segment slots | Ordered first-writer publication |
| `b5379433` | Added dynamic straggler detection and races | Blocking-segment recovery |
| `83dbc315` | Added sticky provider failure weighting/cooldown | Avoid repeatedly slow providers |
| `6e193757` | Bounded and coalesced urgent segment races | No retry amplification |
| `81f371f9` | Fixed pump leaks, EOF hangs, and unnecessary tail pipelines | Completion/lifetime |
| `a9b3e5d9` | Added coverage for requeueing after streaming-permit timeout | Dequeued-job ownership |
| `55c7efe9` | Kept the last retry worker alive through monitor publication | Cancel-before-publish race |
| v0.12.11 | Retries provider-local cancellation when caller/job tokens remain live | Cancellation ownership and null-slot prevention |
| v0.12.12 | Distinguishes output-channel backpressure from a missing ordered slot after workers finish | Slow-consumer range truncation |

### Provider fallback and connection health

| Commit | Change | Why it matters to playback |
|---|---|---|
| `b963ae17` | Introduced multiple providers | Segment fallback |
| `cb33dfad`, `b6261303` | Added latency-aware balancing and reduced provider stalls | Faster failover |
| `6892d48a` | Reapplied connection-pool exhaustion and permit-leak fixes | Avoid pool starvation |
| `81f94ab7` | Offered excluded providers as a last resort | A single/all-excluded provider remains usable |
| `f6e3d8c0` | Made `UsenetProtocolException` part of the unhealthy-connection taxonomy | Desynchronized connections are replaced |

`MultiProviderNntpClient` tries the provider list after a provider-local cancellation, but if every
candidate fails it rethrows the captured `OperationCanceledException` unchanged. v0.12.11 handles
that exception at the segment retry layer without confusing it with monitor preemption.

### Shared streams, permits, and lifecycle

| Commit | Change | Why it matters to playback |
|---|---|---|
| `ca13cbed`, `f609370e` | Added and hardened the global buffered-stream cap | Bounds retry-storm memory |
| `4aa99f90`, `37b48c4a` | Added and integrated shared stream entries | Reuses nearby reads |
| `b504d85e`, `b5109581` | Corrected shared pump ownership and request-token coupling | Prevents pump cancellation/leaks |
| `15582d4c` | Added idle timeout for orphaned buffered streams | Releases abandoned resources |
| `8ecba418`, `aa746bfd` | Tracked limiter leases and held permits through stream disposal | Correct capacity accounting |
| `b68fbbe4`, `f1e42c8a` | Isolated provider context per segment/shared entry | Prevents cross-worker selection races |
| `408f5f55`, `51e8b41f` | Corrected attach counting and keyed shared entries by region | Independent seeks do not collide |

### Multipart, range, and EOF correctness

| Commit | Change | Why it matters to playback |
|---|---|---|
| `a8848f6a` | Fixed seeking in `CombinedStream` | Correct multipart positioning |
| `26034b34` | Fixed combined-stream disposal | Prevents premature part teardown |
| `7e57146c` | Bounded buffered prefetch by requested HTTP range end | Avoids wasted tail pipelines |
| `dca490e6` | v0.8.0 multipart/RAR fixes: exact decoded offsets, unified serving, private fallback for lagging readers | Byte-exact archive playback |
| `eae4a562`, `d30adca4` | Hardened ordinary and archive ranged reads against corrupt yEnc segments | Correct recovery/failure behavior |
| `a622a6d5` | Corrected suffix-byte ranges on `/view/` and WebDAV | Jellyfin/Matroska trailing-Cues reads |

### Memory and prefetch controls

| Commit | Change | Why it matters to playback |
|---|---|---|
| `737530f7`, `7a421f65` | Reduced streaming buffers and hardened OOM behavior | Lower resident memory |
| `e5293916`, `351ec0ae` | Drained undisposed slots and simplified segment allocation | No retained segment leak |
| `f2227ad0` | Tied prefetch to reader position | Workers cannot fetch the whole file |
| `87f9c99b`, `48a949cf` | Established minimum prefetch in useful byte terms | Avoids under-buffering large/high-latency streams |
| `e74c8398` | Replaced `ArrayPool.Shared` with a byte-bounded segment pool | Predictable retained memory |
| `4ebe10ff`, `6c028580` | Derived sizing from heap ceiling and bounded large-segment windows by bytes | Heap-aware concurrency |

### RAR next-volume pre-warming

`48879d03` introduced next-volume pre-warming to remove recurring one-to-three-second pauses at
natural RAR boundaries. The follow-up sequence is part of the feature and should be reviewed as a
unit:

- `f5e99f3a` — concurrency and lifecycle gaps;
- `5370899f` — second-round race fixes;
- `6d4fd531` — cancellation-source race and stronger retry coverage;
- `cecc6d61` — active stream's own pre-warm gets slot priority;
- `6cd0c99e` — coalesced live read honors its own token;
- `066ee42f` — live reads no longer inherit pre-warm slot waiting;
- `c7f0a08d` — memoized-task cleanup driven by the task itself;
- `8dc867b2` — terminally failed memoized tasks detected inline.

## Production incidents that established signatures

### v0.12.7 — *Alone* S13E08

The backend logged:

```text
[BufferedStream] Ordering task timed out. NextIndexToWrite=61, TotalSegments=68
```

The final worker could resume synchronously inside monitor cancellation, see both queues empty, and
exit before the monitor published the replacement. `55c7efe9` added the publication handshake and a
deterministic inline-cancellation regression.

### v0.12.11 — *Planet Earth III* S01E02 cancellation hypothesis

One Synology playback returned 20 prematurely truncated 100 MB ranges. Four occurred in the final
seven minutes where video visibly froze while audio continued. Each range had the old ambiguous
`Ordering task timed out` message, with no terminal segment error, container restart, OOM, NAS
saturation, or transcoder failure. At the time this was interpreted as an unfilled slot. The
v0.12.12 capture below showed that the same message also represented a healthy ordering writer
blocked by a full output channel, so the Planet Earth logs alone did not prove the slot was null.

A real defensive gap still existed: a provider-originated `OperationCanceledException` could reach a
worker while both stream and job tokens remained live. The old worker catch treated every
cancellation with a live stream as monitor preemption, assumed a replacement already existed, and
dropped the dequeued segment. v0.12.11:

- retries this condition in `FetchSegmentWithRetryAsync`;
- excludes the last identified failed provider as a preference on the next attempt;
- defensively requeues the segment if an unrequested cancellation escapes the retry layer; and
- retains the `55c7efe9` rule that a genuinely cancelled job is replaced only by the monitor.

The regression in `backend.Tests/StreamingPermitRequeueTests.cs` traverses:

```text
BufferedSegmentStream
  -> MultiProviderNntpClient
  -> MultiConnectionNntpClient
  -> provider transport
```

It cancels the first provider attempt while the caller token is live and requires complete,
byte-exact output on retry. This remains valid hardening, but the next production playback emitted
zero provider-cancellation events and established a different dominant cause.

### v0.12.12 — *The Secret Life of Pets 2*

The deployed v0.12.11 build reproduced 13 ordering timeouts during roughly one hour of playback.
There were zero `PROVIDER CANCELLATION`, unrequested-cancellation requeue, segment-fetch error,
graceful-degradation, permanent-failure, OOM, restart, or critical-worker events.

Every correlated 145-segment buffered range timed out 32–34 seconds after its `PREFETCH WINDOW`
creation log. Fetch workers needed only 2–4 seconds, after which the old unconditional
`orderingTask.WaitAsync(30s)` expired. The shared-stream `Pumped` byte totals matched the reported
`NextIndexToWrite`: the ordering task had successfully written until its 60-segment output channel
filled and was waiting for playback-rate consumption. The timeout then completed the channel and
disposed the unwritten tail, directly truncating the range.

v0.12.12 publishes whether the ordering writer is blocked on its consumer. Once workers finish:

- progress or a blocked output-channel write continually resets the no-progress deadline;
- a live, slow consumer may take as long as needed to drain the bounded channel; and
- only 30 seconds with no index progress and no consumer backpressure triggers the missing-slot
  safeguard.

The accelerated regression fills the output channel with an instantaneous provider, pauses the
consumer beyond a shortened deadline, and then requires every segment byte in order.

## Regression-test map

When changing streaming behavior, run the full backend suite and pay particular attention to:

- `backend.Tests/StreamingPermitRequeueTests.cs`
- `backend.Tests/BufferedSegmentStreamPrefetchWindowTests.cs`
- `backend.Tests/BufferedSegmentStreamDisposeTests.cs`
- `backend.Tests/ProviderSelectionExclusionTests.cs`
- `backend.Tests/SharedStreamEntryTests.cs`
- `backend.Tests/SharedStreamManagerTests.cs`
- `backend.Tests/CombinedStreamPrewarmTests.cs`
- `backend.Tests/BufferedSegmentStreamPrewarmSlotPriorityTests.cs`
- `backend.Tests/NzbFileStreamPrewarmCoalescingCancellationTests.cs`
- `backend.Tests/NzbFileStreamConstructionRetryTests.cs`
- `backend.Tests/GetWebdavItemSuffixRangeTests.cs`
- `backend.Tests/GetAndHeadHandlerPatchSuffixRangeTests.cs`

Commands:

```bash
dotnet test backend.Tests/NzbWebDAV.Tests.csproj --no-restore --verbosity minimal
docker build -t local/nzbdav:3 .
```

For controlled local reproduction without modifying the NAS deployment, also see
[`docs/repro-harness.md`](docs/repro-harness.md).

## Known defense-in-depth opportunities

These are not established causes of the incidents above and should not be bundled into a fix without
new evidence:

1. Normalize lower-level connection timeouts that still surface as `OperationCanceledException` to a
   dedicated timeout exception where the originating layer can distinguish them reliably.
2. If the new backpressure-aware guard confirms a genuinely absent effective-range slot, propagate an
   explicit HTTP failure instead of ending the channel after the diagnostic. This would make any
   future ownership bug louder to the client as well as in logs.
3. Add a combined adversarial test where provider-local cancellation and monitor cancellation race
   each other. Existing tests cover each ownership path independently.
4. Add request-level telemetry that records expected versus emitted range bytes and the first missing
   segment index, making client-visible truncation directly searchable.

## Before declaring a future stall fixed

- Reproduce or establish a deterministic failure signature.
- Identify which invariant was violated and who owned the missing work/resource.
- Add a regression that fails for the old mechanism, preferably through the production wrapper chain.
- Run the full backend suite.
- Build `local/nzbdav:3`.
- Deploy only with explicit approval.
- Replay the affected content and verify complete ranges, no ordering timeout, no unexpected
  cancellation warnings, and no regression at RAR-volume boundaries or during seeks.
