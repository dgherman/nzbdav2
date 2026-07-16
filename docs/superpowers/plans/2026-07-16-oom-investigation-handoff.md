# OOM investigation — handoff (2026-07-16)

State of the hunt for the streaming OOM that kills the backend with
`Internal CLR error (0x80131506)` during large-file playback.

**Read [#19](https://github.com/dgherman/nzbdav2/issues/19) first.** It has the measurements. This
document is the surrounding context: what shipped, what was wrong, and what not to repeat.

## Bottom line

The OOM is **LOH churn at ~109 MB/s (71% of all allocation)**, refilling the 4 GiB
`DOTNET_GCHeapHardLimit` from a stable ~1.15 GB floor every ~25 seconds. At the top of that cycle
there is no headroom, and an allocation failing inside a finalizer produces the fatal CLR error.

It is **not** a leak, and it is **not** the connection-pool stats event that #14 was reopened to
correct.

Unexplained and the thing to chase: **~7 MB of LOH garbage per MB streamed** (~109 MB/s LOH against
~15 MB/s delivered). Segments are ~700 KB and the LOH threshold is 85,000 bytes, so every
segment-sized buffer is an LOH allocation by definition — but 7× amplification is not accounted for.

## Three wrong diagnoses, and why each survived

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
