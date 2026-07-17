# OOM investigation — handoff (2026-07-16)

> **Diagnosed, NOT fixed.** The cause is **unbounded prefetch**, not an `ArrayPool` pathology:
> fetch workers park completed ~1MB buffers in `segmentSlots` (sized to the whole file) with no
> gate tied to the reader, so the resident set scales with **file size** rather than with
> `bufferSegmentCount`. The pool was reusing correctly the whole time — distinct arrays (303)
> tracked the peak working set (299) almost exactly.
>
> **PR #21 bounded the window, fixed the memory on the NAS, and broke playback. It was reverted.**
> **Why it broke playback is NOT known.** This document previously claimed the prefetch was
> "load-bearing" via `SharedStreamEntry.TryAttachReader` and a 2-slot semaphore. **Both halves of
> that story are false and were disproved on 2026-07-16** — see "Five wrong theories" and
> "Corrections (2026-07-16, later session)" at the bottom. Do not build on it.
>
> **#22 is not an independent bug — it is this bug's OOM-recovery storm.** Also settled below.

State of the hunt for the streaming OOM that kills the backend with
`Internal CLR error (0x80131506)` during large-file playback.

**Read [#19](https://github.com/dgherman/nzbdav2/issues/19) first.** It has the measurements. This
document is the surrounding context: what shipped, what was wrong, and what not to repeat.

## Bottom line

> **Rewritten 2026-07-16.** This section used to conclude that `ArrayPool<byte>.Shared.Rent`
> "misses on roughly every segment" and ask *why the pool misses*. **That was wrong theory #4** and
> it is preserved only under "Three wrong diagnoses" below. The pool is innocent: distinct arrays
> (303) ≈ peak checked out (299). **Do not go looking for a pool pathology.**

**The OOM is unbounded prefetch.** Fetch workers park completed ~1MB buffers in `segmentSlots`,
which is sized to the whole file, and nothing gates them on the reader — the only bounded queue
(`_bufferChannel`) sits *downstream*. So resident memory scales with **file size**, not with
`bufferSegmentCount`. A large enough file reaches the 4 GiB `DOTNET_GCHeapHardLimit` no matter how
the buffers are pooled.

**Caught in the act on v0.11.4** (2026-07-16, clean log — 0 `CORRUPTION DETECTED`, 0 `Invalid NNTP`,
0 breaker trips):

```
15 × [BufferedStream] OOM during fetch   (6 at 22:58:35, 9 at 22:58:36)
1 job. Segments 4859–4889 of 6924.
```

A 6924-segment file wants ~6.9 GB resident; the racing workers all hit the wall inside ~2 seconds,
in a tight band of consecutive indices. That is the mechanism, on production, not in a harness.

**The ~109 MB/s LOH churn is real but it is the symptom**, not the cause — it is what
file-size-scaled buffering looks like from the GC's side. It is **not** a leak, and **not** the
connection-pool stats event #14 was reopened to correct.

**#22's "unexplained" 103–1104 MB/s bursts are this bug's own OOM-recovery storm** — each OOM'ing
worker forces a blocking compacting Gen2 (`HandleOomPressure`, not single-flighted) and backs off,
which is why the network goes quiet during them. Fixing this removes #22. See "Corrections" at the
bottom.

**Status: NOT fixed.** PR #21 bounded the window, fixed the memory on the NAS, broke playback, and
was reverted. **Why it broke playback is unknown** — the "load-bearing / 2-slot cap" explanation is
false (theory #5). The fix direction is still right; it is blocked on a 2-concurrent-stream
reproduction through `SharedStreamManager`, which has never existed.

**The harness reproduces the memory growth** (400 MB threw `OutOfMemoryException`; bounding made
peak resident flat across 200/400/1000 MB) with no NAS access. It **cannot** reproduce the playback
break: one stream, no `SharedStreamManager`, no idle connections, one provider.

## Three wrong diagnoses, and why each survived

> **There are now five.** #4 (the `ArrayPool` pathology) and #5 (the "load-bearing prefetch /
> 2-slot cap") were both written into this document as conclusions and both were disproved. The
> full list is under "Five wrong theories" at the bottom. This section is theories #1–3 as
> originally recorded.

Worth reading before forming a fourth. Each was stated confidently and each was wrong.

1. **"Stream retention leak" (F9).** Disproved by `nzbdav_shared_stream_active_entries`, which never
   exceeded 1 and reads 0 during the failure.
2. **"Retention growing 1:1 with bytes streamed"** (#14 as originally filed). Disproved by the heap
   falling back to a stable floor — that is churn, not retention.
3. **"Stats-event churn is the dominant allocator"** (#14 as reframed, and the reason for PR #17).
   Disproved by generation-level metrics: the stats messages are ~61,683 bytes, **below the 85,000
   byte LOH threshold**, so they were Gen0 garbage. Gen2 holds 29 MB. They were never the heap.

**The common root of all three:** `dotnet_total_memory_bytes` (`GC.GetTotalMemory(false)`) includes
uncollected garbage. Under Server GC with a 4 GiB budget, a high allocation rate looks *identical* to
a leak — the heap sits near the cap because the GC has been told it may. Two sessions were spent
inferring from that number and from allocation counters.

**Use `system_runtime_dotnet_gc_last_collection_heap_size{gc_heap_generation="..."}` instead.** It is
already exposed, it is not in the default `dotnet_*` set, and it answers "live or garbage, and
which generation" in one call:

```bash
ssh -o RequestTTY=no -o RemoteCommand=none syno \
  'curl -s http://localhost:8080/metrics | grep -E "^system_runtime_dotnet_gc"'
```

## What shipped in v0.11.4 (PR #17, merged as e91f3d7)

Three things, all sound on their own terms — but **none of them fixes the OOM**:

- **#15 — mock harness repair.** It served one hardcoded yEnc article for every message ID, so every
  segment claimed byte 0 and a `--size=50` run measured 0.68 MB. Now measures the file it is asked
  for. This is the rig for any streaming measurement that does not touch the NAS.
- **#16 — provider selection.** `GetBalancedProviders` dropped excluded providers entirely, so a
  single-provider setup failed a stalled segment outright with a false *"no usenet providers
  configured"*. Restored the last-resort tier `GetOrderedProviders` always had.
- **#14 — stats event.** Skips building the UI message when no websocket is attached (61,683 → 0
  bytes) and coalesces to 4 pushes/sec per provider. **The bail-out never fires in production** (see
  below). The debounce does apply.

### The trap that made #14's result look real

`frontend/server/websocket.server.ts:59` (`initializeWebsocketClient`) opens a **persistent
authenticated websocket from the frontend node server to the backend at startup** and holds it
forever. It is a relay: it subscribes on behalf of browsers and fans messages out. So the backend
always has ≥1 authenticated socket and `HasSubscribers` is never false.

The harness is a CLI tool with no websocket, so it exercised the bail-out path **production never
takes**, and reported a win that does not exist there. Any future "skip work when nobody is
listening" optimisation has the same trap: verify against the relay, not the harness.

## Environment facts that matter

- `DOTNET_GCHeapHardLimit=0x100000000` (4 GiB), `DOTNET_GCServer=1`; container limit 8 GiB.
- **7 providers** configured — so `BufferedSegmentStream.UpdateUsageContext()`'s fan-out to every
  provider's pool is 7×, not the 1× the single-provider harness models.
- **Clients are Stremio** (web/iOS/Android), *not* Plex. Many range requests across platforms, not
  one sequential reader. Earlier reasoning that assumed a single player model is suspect.
- Segments ~700 KB. `ArrayPool<byte>.Shared`'s largest bucket is 1 MB; rent above that and it
  allocates un-pooled and `Return` silently discards.

## NAS access

```bash
ssh -o RequestTTY=no -o RemoteCommand=none syno 'sudo /usr/local/bin/docker ...'
```

- **Always strip ANSI before grepping:** `sed 's/\x1b\[[0-9;]*m//g'`.
- Log format is `[HH:MM:SS WRN]` — a level regex must be `WRN]`, not `\[WRN\]`.
- NAS logs are UTC; local is EDT (UTC−4).
- Verify the running build from the startup banner (`BUILD vYYYY-MM-DD-...`) before trusting any
  measurement.
- Grafana Cloud: `mmm314.grafana.net`, stack 193434, token at `~/.grafana_api_token`, proxy path
  `/api/datasources/proxy/uid/grafanacloud-prom/api/v1/query_range`. **Python urllib fails cert
  verification — use curl.**

## Known-open, filed with evidence

- **[#19](https://github.com/dgherman/nzbdav2/issues/19)** — LOH churn. The real cause. Start here.
- **[#14](https://github.com/dgherman/nzbdav2/issues/14)** — reopened and corrected; decide whether
  to keep the debounce under it or close it as a micro-optimisation.
- **[#18](https://github.com/dgherman/nzbdav2/issues/18)** — seek latency is connection-pool
  contention, not network: `Connection Acquire 122,692 ms (99%)` vs `Network Read 208 ms (0%)`. Each
  seek stacks a new full-width stream onto a pool the outgoing stream still holds. Worker timeouts
  from acquire starvation then call `RecordFailure()`, so local contention trips the *provider's*
  breaker for up to 300s. NAS shape (25/stream, 30 total) has the same overlap.

## Capturing an allocation profile (this is how the call site was found)

`dotnet-gcdump` is the **wrong tool** — it triggers a blocking Gen2 and captures *live* objects, so
all the garbage vanishes before it looks and it shows only the ~1.15 GB floor. It answers "what is
retained", and nothing is retained.

Use EventPipe's `GCAllocationTick` (fires every ~100 KB, carries the type name and the LOH flag). No
tool needs to attach — it is startup env vars, so it works in a throwaway harness container:

```
DOTNET_EnableEventPipe=1
DOTNET_EventPipeOutputPath=/config/trace.nettrace
DOTNET_EventPipeConfig=Microsoft-Windows-DotNETRuntime:1:5    # GC keyword, Verbose
```

Analyse with TraceEvent (`Microsoft.Diagnostics.Tracing.TraceEvent`):

```csharp
var etlx = TraceLog.CreateFromEventPipeDataFile(path);   // stacks live on TraceLog,
using var traceLog = new TraceLog(etlx);                 // NOT on raw EventPipeEventSource
var source = traceLog.Events.GetSource();
source.Clr.GCAllocationTick += d => { /* d.AllocationKind, d.TypeName, d.CallStack() */ };
source.Process();
```

A ~200 MB run produces an ~11 MB trace. Match the tool's arch to the image
(`aka.ms/dotnet-trace/linux-musl-arm64` for a local Apple Silicon build; `-x64` for the NAS).

## Benchmark harness

```bash
docker build -t local/nzbdav-leak:test .
docker run --rm -e CONFIG_PATH=/config -v "$PWD/cfg:/config" -w /config \
  --entrypoint /app/backend/NzbWebDAV local/nzbdav-leak:test --db-migration
docker run --rm -e CONFIG_PATH=/config -v "$PWD/cfg:/config" -w /config \
  --entrypoint /app/backend/NzbWebDAV local/nzbdav-leak:test \
  --test-full-nzb --mock-server --size=300 --connections=30
```

Must run in the image — `rapidyenc` is a Linux-native library and the harness cannot run on macOS.
Prints an ALLOCATION block (total allocated, alloc per MB read, Gen0/1/2, heap now).

**Two caveats:** it has no websocket (so it cannot model the relay), and it runs a **single**
provider (so it cannot model the 7× fan-out). Neither limitation is fixed.

## How the cause was actually found (v0.11.5 — which did NOT resolve it)

> **Title corrected 2026-07-16.** This section used to be called "How it actually resolved". It did
> not resolve: v0.11.5 was reverted (see "Deployed, and reverted"), the OOM is live on v0.11.4
> today, and everything below the "The fix" heading describes a **reverted** change. What survives
> is the *diagnosis* and the *method*.

The fourth theory — the one this document proposed above — was also wrong, and wrong in the same
way as the first three: it was inferred from reading code, and it named the pool as the culprit.

**The pool was fine.** Instrumenting the rent path (`SegmentBufferPoolDiagnostics`, set
`NZBDAV_POOL_DIAG=1`) with rents, returns, distinct array identities and the peak checked-out count
settled it in one run:

```
Rents:                   598
Returns:                 307
Peak checked out:        299   <- working set the pool must satisfy
Distinct arrays:         303   <- fresh allocations
Pool reuse rate:       49.3%
```

Distinct arrays (303) ≈ peak checked out (299). The pool allocated **one array per slot of the
working set and reused it thereafter** — ideal behaviour. There was no trim loop, no bucket
overflow, no TLS problem. `Rent` "missing on every segment" in the EventPipe trace was the pool
correctly filling a working set that was ~300 buffers wide.

A finalizer counting segments collected while still holding a buffer reported **0**: nothing
leaked either. The 291 unreturned buffers were simply **still checked out** — alive, referenced,
sitting in stream channels and slots.

### The actual cause

The backpressure chain has a hole:

```
producer -> segmentQueue(60) -> workers(30) -> segmentSlots(UNBOUNDED, sized to the whole file)
         -> orderingTask -> _bufferChannel(60) -> reader
```

`_bufferChannel` bounds only the **ordering task**, which is *downstream* of the slots. Nothing
tied the fetch rate to the read rate, so the workers raced to the end of the file, each completed
segment parking a ~1MB pooled buffer in a slot. **Resident memory scaled with file size, not with
`bufferSegmentCount`.** The comment at the channel construction ("250 segments = ~500MB") was
describing a bound that did not exist.

Proof, as a unit test (`BufferedSegmentStreamPrefetchWindowTests`): with the reader stopped after
**one** segment, the fetchers pulled **500 of 500** segments anyway.

### The fix

The producer holds a segment back until the reader is within `bufferSegmentCount + connections` of
needing it. `_nextIndexToRead` is always inside the window, so the segment the reader is waiting on
can never be gated.

| measurement (mock harness, 30 connections) | before | after |
| --- | --- | --- |
| peak buffers resident, 200 MB file (293 segments) | 299 — *the whole file* | 179 |
| peak buffers resident, 400 MB | **OutOfMemoryException** | 183 |
| peak buffers resident, 1000 MB | — | 185 |
| LOH after last GC, 200 MB | 376 MB | 238 MB |
| allocation per MB read, 200 MB | 2.8 MB | 1.9 MB |
| segments fetched to deliver 293 | 427 | 294 |

Peak resident is **flat across 200/400/1000 MB** — memory is now a function of configuration, not
of file length. That flatness is the result; the LOH reduction is a side effect of it.

### What is still open

- **Throughput.** Harness sequential speed reads ~13 MB/s after vs ~23 MB/s before. The comparison
  is confounded and the harness is a poor instrument for it: ~99% of fetch time there is connection
  acquire (**#18**), the mock provider's breaker trips ~28 times per run on **baseline** too, and
  the run-to-run spread is 7.6–34.1 MB/s. The baseline bought its number by fetching 1.43× the
  segments and holding the whole file in RAM — the bug itself. **#18 is the real throughput
  limiter** and is untouched.
- **Window vs memory tradeoff.** The window costs `window × 1 MB` per live stream (~90 MB at 30
  connections), and each buffer is 1 MB serving ~700 KB because 720,896 rounds up to
  `ArrayPool`'s 1 MB bucket. Right-sizing that is worth ~30% and is not done.
- **Not verified on the NAS.** The harness models neither the websocket relay nor the 7× provider
  fan-out. The mechanism (memory ∝ file size) is provider-count-independent, but the numbers above
  are harness numbers.

### The lesson, again

Four theories, four wrong, all from reading code. The measurements were right every time. The one
that cracked it took ~20 lines of counters. **Instrument the thing before theorising about it** —
and note that "the call site is identified" is not the same as "the cause is identified": the
allocation trace named the exact line, and the line was innocent.

## Deployed, and reverted (2026-07-16, same day)

PR #21 shipped to the NAS and was rolled back within the hour. **Read this before re-attempting.**

### The memory fix worked. On production.

With bounded prefetch and 30 busy connections: **20–26 MB/s allocation, heap stable 353–512 MB**,
against 153 MB/s and the heap cycling 1.15 GB → 4 GiB every ~25s before. No OOM in ~27 min. When
the client stopped pulling, connections went idle and allocation fell to ~1 MB/s. The diagnosis and
the memory result are **confirmed on real hardware**.

### And it broke playback.

The **second concurrent video would not play at all** — multiple different shows. In 8 minutes: 14
`CORRUPTION DETECTED`, 21 `Invalid NNTP Response`, 58 circuit-breaker trips. Shared-stream misses
`existing_entry_unattachable=43` / `position_out_of_range=43` against 22 hits. Rollback to v0.11.4
→ two videos play fine immediately.

### The coupling nobody knew about — WRONG, see corrections below

> **Struck 2026-07-16.** Both halves of this section are false. `s_concurrentStreamSlots` is **8** in
> production, not 2, and the attach misses it cites occur on the *working* build at the same ratio.
> Kept only so the next reader recognises the shape of the error. **Why PR #21 broke playback is
> unknown.**

~~`SharedStreamEntry.TryAttachReader` accepts a reader only within
`[ValidRangeStart, WritePosition + ringBufferSize]` — a function of how far the pump has run
ahead of the reader. Bounding prefetch ties the pump's frontier to reader consumption, so joining
readers fail to attach, fall back to a *private* `BufferedSegmentStream`, and those need a slot
from `s_concurrentStreamSlots` — which is `new SemaphoreSlim(2, 2)`. Two videos exhaust it.~~

~~**The unbounded prefetch is load-bearing.**~~ (Unverified when written; disproved since.)

### Still unexplained — RESOLVED, see corrections below

> **Resolved 2026-07-16.** The 103–1104 MB/s bursts are **this bug's own OOM-recovery machinery**
> (`HandleOomPressure`), not a second allocator. See "#22 is #19's OOM storm" below.

### The harness gap that let this ship

The harness runs **one stream, no `SharedStreamManager`, no idle connections, one provider**. It
could not have caught this. Preconditions for the next attempt:

1. Reproduce **2 concurrent streams through `SharedStreamManager`**.
2. ~~Treat the attach range and the 2-slot cap as part of the window change.~~ **Struck** — the
   2-slot cap does not exist (it is 8), and the attach misses are normal. Instead: **find out what
   actually broke playback**, because as of 2026-07-16 nobody knows.
3. Model **idle** connections — bounded prefetch newly lets them idle, and idle connections go
   stale (`Health check failed for idle connection (idle: 455s)`). Pre-fix they were never idle
   because the workers raced constantly.

Confound, stated: the revert also restarted the container, resetting pools and breakers. Against
that, the failure persisted ~25 min across multiple shows. Strong, but n=1.

### The lesson, a third time

This document already said: *"Do not claim a production win from a harness number without
confirming the deployment takes that path — a previous fix measured green and is inert in
production for exactly this reason."* The next session read that rule, quoted it, and then shipped
a fix validated on a single-stream harness into a multi-stream production path. The memory numbers
were true; the harness simply could not see the thing that mattered. **A green harness number is
evidence about the harness.**

## Corrections (2026-07-16, later session)

Three claims above are wrong. All three were settled by reading the **running v0.11.4 container** —
no repro, no deploy, no trace. The container had the answer the whole time.

### #22 is #19's OOM storm, not a second bug

v0.11.4, 2h uptime, **clean log** — 0 `CORRUPTION DETECTED`, 0 `Invalid NNTP`, 0 breaker trips:

```
15 × [BufferedStream] OOM during fetch   (6 at 22:58:35, 9 at 22:58:36)
1 job. Segments 4859–4889 of 6924.
```

That is #19's mechanism caught in the act: unbounded prefetch races a 6924-segment (~6.9 GB if
resident) file into the 4 GiB `GCHeapHardLimit` and every racing worker hits the wall within the
same ~2 seconds, in a tight band of consecutive segment indices.

Each of those workers then runs `HandleOomPressure()` (`BufferedSegmentStream.cs:1428`):

```csharp
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
GC.WaitForPendingFinalizers();
```

**Deliberately not single-flighted** — the comment above it explains why, and that reasoning still
stands. So 15 OOMs ⇒ 15 blocking compacting Gen2 collections in ~2s, plus `WaitForOomCooldownAsync`
suppressing fetches 750ms and a `500 + attempt*500` ms retry backoff.

That accounts for three of #22's four signatures:

| #22 observed | mechanism |
| --- | --- |
| 13 Gen2 in 15s | one forced blocking Gen2 per OOM'ing worker |
| CPU 342% | repeated blocking **compacting** Gen2 on a multi-GB heap |
| busy=1–3 | OOM cooldown + retry backoff **suppress fetching** |

The bursts look like "allocating with no network" because **the OOM handler is what stopped the
network**. #22 is downstream of #19, not a rival to it — and the candidate list it proposed (RAR
`Unpack`/`RarStream`/`RarVM`, `AesDecoderStream`, yEnc, `CombinedStream.DiscardBytesAsync`, Par2)
is aimed at a path that is not involved.

**Still genuinely open:** the **1104 MB/s** figure itself. `GC.Collect` does not allocate. The
metric is single-line, so it is not a sampling artifact of the loop's `awk`. The retry loop
re-renting ~1MB per attempt is a candidate and is **not** confirmed. If anything here deserves an
EventPipe `GCAllocationTick` trace, it is this — **aimed at v0.11.4 during an OOM storm**.

### The 2-slot semaphore does not exist

`new SemaphoreSlim(2, 2)` at `BufferedSegmentStream.cs:23` is a **field initializer**, unconditionally
overwritten at startup by `Program.cs:214`:

```csharp
BufferedSegmentStream.SetMaxConcurrentStreams(configManager.GetMaxConcurrentBufferedStreams());
```

Production config `usenet.max-concurrent-buffered-streams = **8**`. Log shows **0** occurrences of
"No semaphore slot available". `ConfigManager.GetMaxConcurrentBufferedStreams()` already documents
the 2→8 change — *"Default 8 (was 2): a single multipart/RAR playback needs a buffered-stream slot
per active part, plus the player's parallel head/tail probes — 2 caused 'No semaphore slot
available' and stalls."* — i.e. the fix **predates this investigation**, and PR #21's post-mortem
re-derived the old failure from a stale constant it read out of the source.

### The attach misses are normal

v0.11.4 — the build that plays two videos fine:

```
hits 22 | no_entry 36 | position_out_of_range 23 | existing_entry_unattachable 23
```

PR #21 read `position_out_of_range=43 / existing_entry_unattachable=43 vs 22 hits` as its smoking
gun. The **working** build has the same pattern. Caveat, stated: these are cumulative counters over
different uptimes and workloads, so v0.11.5's miss *rate* may still have been higher — but the miss
*kind* is not novel, and the mechanism that was supposed to convert misses into unplayability (slot
exhaustion) never fired.

### A theory, flagged as a theory

`GC.WaitForPendingFinalizers()` called concurrently from ~15 threads under memory pressure is a
candidate for converting a *survivable* OOM into `Internal CLR error (0x80131506)` — which is
specifically **an allocation failing inside a finalizer**. If that holds, the recovery machinery
manufactures the crash it exists to prevent. **Unverified.** It is theory #6; theories #1–5 were all
wrong. Measure it.

### Five wrong theories

1. Stream retention leak.
2. Retention growing 1:1 with bytes streamed.
3. Stats-event churn is the dominant allocator.
4. `ArrayPool` pathology / the pool misses.
5. **Prefetch is load-bearing via `TryAttachReader` + a 2-slot cap.**

#1–4 were read from code and disproved by measurement. **#5 was also read from code** — including
a constant that the running process overwrites at startup. The pattern is not "we lacked data", it
is **"source was read as if it were runtime state."** #5 was written into this doc, PR #21's
post-mortem, issue #22's framing, and the project memory, and was believed for a full session.

**Check the running process, not the source.** `docker exec … /metrics`, the config table, and the
container's own log settled all three corrections above in about ten minutes.
