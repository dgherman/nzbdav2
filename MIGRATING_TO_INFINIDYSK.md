# Migrating from nzbdav2 to infinidysk

This is an **unofficial, community-maintained** migration path. It is not supported or
endorsed by the infinidysk project. infinidysk's own documentation names `nzbdav-dev/nzbdav`
v0.6.4, `Pukabyte/nzbdav`, and `qooode/nzbdavex` as **community-validated** migration sources
(per infinidysk's own docs wording - not an officially guaranteed migration path even for
those) - nzbdav2 is **not** among them.
nzbdav2 diverged from that shared ancestor a long time ago (33 nzbdav2-only migrations vs 37
infinidysk-only migrations since the last shared migration,
`20251113081523_Populate-Usenet-Providers-Config`), so a raw `dotnet ef database update`
against a nzbdav2 database will not work, and infinidysk's own importer does not understand
nzbdav2's schema.

## Why migrate at all

The upstream project nzbdav2 is a fork of (`nzbdav-dev/nzbdav`) is no longer maintained.
infinidysk is the actively-developed successor: it absorbs and extends nzbdav's feature set
(usenet provider pooling, health repair, Arr integration, watchtower/list-source automation,
and more), and is where new fixes and features are actually landing. nzbdav2 itself is a
personal fork with its own bug fixes and UI/observability additions layered on the old base -
it is not a drop-in alternative to infinidysk, and staying on it means missing everything
infinidysk has built since the fork point.

This repo ships a `tools/MigrateToInfinidysk` console tool (built with TDD, tests under
`tools/MigrateToInfinidysk.Tests`) that reads a nzbdav2 `db.sqlite` and writes the equivalent
rows into an already-initialized infinidysk `db.sqlite`, following the compatibility mapping
below. It is **read-only against the nzbdav2 source** and only ever writes to the infinidysk
target database (plus a JSON sidecar archive for data that has no infinidysk equivalent).

It's published as a standalone Docker image, `ghcr.io/dgherman/nzbdav2-migrate-infinidysk` (same
tagging scheme as the main nzbdav2 image - `latest`, `MAJOR.MINOR.PATCH`, `MAJOR.MINOR.x`,
`MAJOR.x` on `main`, or the branch name otherwise), built by
`.github/workflows/docker-publish.yml`. **No .NET SDK or local checkout is required** - it's a
self-contained image you `docker run` the same way you already run nzbdav2 and infinidysk
themselves. Building from source is covered in the appendix at the end of this doc, for anyone
who'd rather do that.

## Before you start: back up

**Tar the entire nzbdav2 `/config` volume before doing anything else.** This tool never
modifies the nzbdav2 database, but you are about to stop the container and start a different
one against a new volume, and mistakes during that process are much cheaper to recover from
with a backup in hand than without one.

```bash
docker stop nzbdav2
tar -czf nzbdav2-config-backup-$(date +%Y%m%d).tar.gz -C /path/to/nzbdav2/config .
```

Keep that archive until you've confirmed the migrated infinidysk library browses correctly and
playback works (see "Verify" below). Do not delete or reuse the old nzbdav2 config volume until
then.

## Procedure

1. **Stop nzbdav2.**
   ```bash
   docker stop nzbdav2
   ```

2. **Start infinidysk fresh against a new, empty config directory** so it runs its own startup
   migrations and creates a fully-migrated `db.sqlite`. Do **not** point it at the nzbdav2
   volume yet. Use a plain bind-mounted host directory for this, the same way you'll pass it to
   the migration tool below (a Docker *named* volume like `infinidysk-config` works fine for
   running infinidysk day-to-day, but its data lives under Docker's internal storage path, not
   a path you can hand to `--source`/`--target` directly - a bind mount to an ordinary host
   directory sidesteps having to look that path up):
   ```bash
   mkdir -p /path/to/infinidysk-config
   docker run --rm -v /path/to/infinidysk-config:/config ghcr.io/infinidysk/infinidysk:latest
   # let it finish its startup migration (watch the logs for the migration-complete message,
   # or check that /path/to/infinidysk-config/db.sqlite exists and has grown past a few KB),
   # then Ctrl-C or `docker stop` the container.
   ```

3. **Stop infinidysk** once its startup migration has finished.

4. **Run the migration tool in `--dry-run` first** (this is the default - it refuses to write
   anything until you pass `--apply`). Bind-mount the nzbdav2 config directory read-only as
   `/source` and the infinidysk config directory (read-write, since `--apply` will write into it
   later) as `/target`, and point `--source`/`--target` at those in-container paths:
   ```bash
   docker run --rm \
     -v /path/to/nzbdav2/config:/source:ro \
     -v /path/to/infinidysk-config:/target \
     ghcr.io/dgherman/nzbdav2-migrate-infinidysk:latest \
     --source /source --target /target
   ```
   Read the per-table report carefully. It shows exactly what will be copied, mapped, or
   skipped, with row counts, and flags:
   - any `DavMultipartFiles` row with a recorded obfuscation key, or with no recorded key at all
     (nzbdav2 gives no reliable way to prove any given row's content definitely isn't obfuscated
     - not even by table membership, since RAR-extracted content can land directly in
     `DavMultipartFiles` with no distinguishing marker; see "What is NOT preserved" below), that
     will be excluded entirely - both the file's metadata and its `DavItems` entry, so it simply
     won't appear in the target library,
   - any `ConfigItems` keys infinidysk doesn't recognize (notably nzbdav2's discrete
     `usenet.host`/`port`/`use-ssl`/`connections`/`user`/`pass` keys - infinidysk consolidates
     usenet provider configuration into a single `usenet.providers` key with a different shape,
     so **you must reconfigure your usenet provider(s) manually in infinidysk's UI after
     migrating** - this is not done for you),
   - a multi-admin conflict if your nzbdav2 database has more than one `Accounts` row with
     `Type = Admin` (infinidysk allows only one; re-run with `--admin-username <name>` to
     choose which one to keep).

5. **Apply it** once the dry-run report looks right - same command, `--apply` added:
   ```bash
   docker run --rm \
     -v /path/to/nzbdav2/config:/source:ro \
     -v /path/to/infinidysk-config:/target \
     ghcr.io/dgherman/nzbdav2-migrate-infinidysk:latest \
     --source /source --target /target --apply
   ```
   This writes into the infinidysk database only, and writes a
   `nzbdav2-migration-archive.json` sidecar into `/path/to/infinidysk-config` on the host (next
   to infinidysk's `db.sqlite` by default, override with `--archive-path <in-container-path>`)
   containing everything that has no infinidysk equivalent - see "What is NOT preserved" below.

   > **If you already ran `--apply` with a tool version older than this repair (before the GUID
   > casing fix): repair your existing target before starting infinidysk.** A bug in earlier
   > versions of this tool wrote GUID-shaped columns (`DavItems.Id`, `DavNzbFiles.Id`,
   > `DavMultipartFiles.Id`, and others) using nzbdav2's original casing instead of the uppercase
   > form infinidysk's own database expects. You'll know you're affected if infinidysk's logs
   > repeat an error like:
   > ```
   > Error migrating usenet-file to blob-store: DavItem with id <guid> not found
   > ```
   > on the same row(s) over and over, and/or playback of files migrated from nzbdav2 (not
   > freshly downloaded ones) fails. If you don't see this, skip this box - it doesn't apply to
   > you.
   >
   > To fix it:
   > 1. **Stop infinidysk first** - repair writes directly to the live `db.sqlite` file, and
   >    infinidysk must not have it open while that happens.
   >    ```bash
   >    docker compose stop infinidysk   # or `docker stop infinidysk`, however you run it
   >    ```
   > 2. **Run the tool in repair mode.** This is a separate mode from the migration itself: the
   >    database file path is a plain argument straight after the flag, not paired with
   >    `--source`/`--target`, and any `--apply`/`--dry-run` you also pass is ignored - repair
   >    mode always writes (nothing else runs alongside it).
   >    ```bash
   >    docker run --rm \
   >      -v /path/to/infinidysk-config:/target \
   >      ghcr.io/dgherman/nzbdav2-migrate-infinidysk:latest \
   >      --repair-guid-casing /target/db.sqlite
   >    ```
   >    It prints exactly what it changed (table, column, row count) before exiting. It also runs
   >    the same pre-commit foreign-key check the rest of this tool uses, so a genuine, unrelated
   >    dangling reference in your target database surfaces as a clear "table X row Y references
   >    missing parent Z" message instead of a bare database error.
   > 3. **Safe to re-run.** Repair only ever rewrites a value that isn't already uppercase to its
   >    uppercase form - rows already correct are left untouched and not reported as changed - so
   >    running it again (or running it on a target that was never affected in the first place) is
   >    a no-op. Run it as many times as you like.
   > 4. **Start infinidysk again** once repair finishes, then continue with the verification steps
   >    below as normal.

6. **Start infinidysk against the now-populated config directory**, the same
   `/path/to/infinidysk-config` bind mount used above (via `docker run` directly, or your usual
   `docker-compose.yml`/Container Manager setup pointed at that same host path).

7. **Trigger `RecreateStrmFilesTask`** to regenerate `.strm` files. This is required: nzbdav2
   and infinidysk compute the `.strm` download-key authentication token differently
   (nzbdav2 uses `SHA256(path + apiKey)`; infinidysk uses `HMACSHA256(key=apiKey, data=path)`),
   so nzbdav2's existing `.strm` files will fail to authenticate against infinidysk until
   they're rewritten. Trigger it via infinidysk's admin API (this is the only supported
   trigger - there is no CLI command or scheduled job for it):
   ```bash
   curl -X POST "http://<infinidysk-host>:8080/api/recreate-strm-files?rewriteAll=true" \
     -H "X-Api-Key: <your infinidysk api key>"
   ```
   Or use the button under infinidysk's Settings -> Maintenance -> "Recreate STRM Files" page
   in the web UI. Progress is reported over the UI's websocket connection.

8. **Verify:**
   - Browse the library in infinidysk's WebDAV mount / UI and confirm your existing content
     shows up with correct names and folder structure.
   - Spot-check playback on a handful of files across different content types (plain files,
     multi-part/RAR-derived files) in Plex/Jellyfin.
   - Check the queue and history pages show your in-flight and completed downloads.
   - Review `nzbdav2-migration-archive.json` for anything flagged as skipped, and decide
     whether you need to act on it (e.g. re-encode/re-obtain content flagged for obfuscation-key
     issues, or manually reconfigure a `ConfigItems` key that wasn't recognized).

Only once you've verified the above should you consider decommissioning the old nzbdav2
volume - keep the backup from step 0 until then regardless.

## What is NOT preserved

The following nzbdav2 data has **no destination in infinidysk** and is archived to the JSON
sidecar (not silently dropped, but not imported into infinidysk's working database either):

- **Any `DavMultipartFiles` row with no recorded obfuscation key - a permanent limitation, not a
  gap this tool closes.** infinidysk's `DavMultipartFile.Meta` has no field for RAR obfuscation
  keys, and has no XOR-deobfuscation support at all, so there is nowhere for RAR-obfuscated
  content to go even if this tool could prove a given file needed it. A *non-null* key obviously
  has nowhere to go and is always skipped. A *null* key is **not** proof the content is safe
  either, and this applies to every `DavMultipartFiles` row, not just ones reached through the
  legacy `DavRarFiles` table: nzbdav2's `RarAggregator` writes RAR-extracted content directly
  into `DavMultipartFiles`, with the exact same `Meta` shape (`AesParams`/`ObfuscationKey`/
  `FileParts`, nothing else) as a row produced by `MultipartMkvProcessor` or `SevenZipProcessor` -
  there is no source-type/provenance column anywhere in nzbdav2's schema, so table membership
  alone cannot prove a row is safe. nzbdav2 detects RAR obfuscation by content-sniffing at read
  time (`RarDeobfuscationStream`, which falls back to content-sniffed default-key detection
  whenever no key was recorded), not from a stored flag, so this tool cannot tell a
  genuinely-unobfuscated file from one nzbdav2 would have needed to sniff at playback - every
  null-key row is conservatively treated the same way. **The affected file's entire entry - both
  its metadata and its place in the file listing - is left out of the target library**, not just
  its playback data; it will not appear at all until you manually re-download or re-process it
  under infinidysk. Every skipped file's ID, recorded key (if any), and full source metadata are
  written to the JSON archive so you can identify and recover them.
- **Bandwidth/provider diagnostics history**: `BandwidthSamples`, `NzbProviderStats`,
  `ProviderBenchmarkResults`. infinidysk has no equivalent tables; this is historical telemetry
  only and does not affect functionality going forward.
- **Analysis history**: `AnalysisHistoryItems` (nzbdav2's per-file analysis run log).
- **Missing-article diagnostics**: `MissingArticleEvents`, `MissingArticleSummaries`.
- **Local links**: `LocalLinks` (nzbdav2's manual local-filesystem link records). infinidysk has
  no equivalent table.
- **A handful of nzbdav2-only `HistoryItems` fields** that don't map to any infinidysk column:
  `IsHidden`/`HiddenAt`, `NzbContents`, `FailureReason`, `IsImported`, `IsArchived`/`ArchivedAt`.
  The rest of each `HistoryItems` row (job name, category, status, timing, etc.) is copied
  normally; only these specific fields are archived instead.
- **`HealthCheckResults.Operation`**: infinidysk's `HealthCheckResults` table doesn't have this
  column. The rest of each row is copied; `Operation` is archived to the sidecar.
- **Segment-level seek optimizations** (`SegmentByteRanges`/`SegmentByteRangesTrusted`) are
  intentionally left unset on every imported row rather than guessed from nzbdav2's stored
  sizes, since this tool cannot reproduce infinidysk's live second-segment probe that its own
  trust provenance depends on. infinidysk will safely re-derive these via header-probed seeking
  on first access - this only affects initial-seek performance, not correctness.
- **`DavNzbFiles.SegmentFallbacks`** (alternate message IDs for segments with duplicate segment
  numbers in the NZB, used as a retry path when a primary article is missing) is **not**
  re-derivable data - it's a literal list of alternate article IDs recorded when the NZB was
  originally queued. infinidysk's `DavNzbFiles` table has no SQL column for it (only
  `Id`/`SegmentIds`), so a plain `DavNzbFiles` row can't carry it. Instead, an NZB file is
  wrapped as a single-part `DavMultipartFiles` row (`FileParts[0].SegmentFallbackIds`, which
  infinidysk *does* read at playback) whenever the source `DavItems` row has a valid `FileSize`
  to build that part's byte range from. Only when there's no valid `FileSize` to wrap around
  (missing `DavItems` row, or `FileSize` null/non-positive) does this fall back to writing a
  plain `DavNzbFiles` row with the fallback IDs archived only (not read at playback) - the
  dry-run report and archive both flag this case with a warning naming the affected file.
- **Legacy `usenet.host`/`port`/`use-ssl`/`connections`/`user`/`pass` config keys.** infinidysk
  replaced these with a single `usenet.providers` key of a different shape. You must
  reconfigure your usenet provider(s) manually in infinidysk after migrating.

## Compatibility reference

| nzbdav2 table | Status | Notes |
|---|---|---|
| `DavItems` | Mapped | Legacy `Type` enum split into `Type`+`SubType`; fixed root IDs merged by ID, not duplicated |
| `DavNzbFiles` | Mapped | Wrapped as a single-part `DavMultipartFiles` row (SubType 203) when the source `DavItems` row has a valid `FileSize`, so `SegmentFallbacks` land in a column infinidysk actually reads at playback; falls back to a plain `DavNzbFiles` row (SubType 201) with fallback IDs archived-only when there's no valid `FileSize` to wrap around |
| `DavMultipartFiles` | Mapped, always skipped when key is null-or-set (permanent limitation) | Skipped and archived with full recoverable payload for *any* recorded obfuscation key, and for *no* recorded key too - table membership alone can't prove a row is RAR-free (`RarAggregator` writes RAR-extracted content directly into this table with no distinguishing field), so a null key is never treated as proof of safety |
| `DavRarFiles` | Mapped, always skipped (permanent limitation) | Always converted into the same `DavMultipartFiles` shape nzbdav2 itself converts them to (SubType 203, matching `DavItemTypeMapper`), but always skipped and archived regardless of key-nullness, for the same reason as `DavMultipartFiles` above. infinidysk has no XOR-deobfuscation support to hand such content to even if this tool could prove it needed one |
| `LocalLinks` | **Incompatible** | Archived to JSON sidecar only |
| `QueueItems` | Mapped | `SortOrder` backfilled with infinidysk's own `ROW_NUMBER() OVER (PARTITION BY Priority ORDER BY CreatedAt, Id) * 1024` formula |
| `QueueNzbContents` | Compatible | Copied unchanged |
| `HistoryItems` | Mapped | Shared columns copied; nzbdav2-only fields archived (see above) |
| `AnalysisHistoryItems`, `BandwidthSamples`, `MissingArticleEvents`, `MissingArticleSummaries`, `NzbProviderStats`, `ProviderBenchmarkResults` | **Incompatible** | Archived to JSON sidecar only |
| `ConfigItems` | Filtered | Only keys infinidysk actually reads are copied; others skipped and reported |
| `Accounts` | Mapped, with conflict check | infinidysk allows only one Admin account; multiple require `--admin-username` |
| `HealthCheckStats` | Compatible | Copied unchanged |
| `HealthCheckResults` | Mapped | `Operation` field archived; rest copied |

## Appendix: building and running from source

You shouldn't need this - the Docker image in the Procedure section above is self-contained and
needs neither a .NET SDK nor a checkout of this repo. This is here for anyone who'd rather build
and run the tool directly instead of pulling the published image (e.g. to test an unreleased
change on this branch).

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a checkout of this repo.

```bash
dotnet run --project tools/MigrateToInfinidysk -- \
  --source /path/to/nzbdav2/config \
  --target /path/to/infinidysk-config
  # add --apply once the dry-run report looks right
```

`--source`/`--target` take plain host paths here (no bind-mount indirection needed, since the
tool isn't running inside a container), otherwise this is identical to the Docker-based
procedure above - same flags, same dry-run-by-default behavior, same archive sidecar.

You can also build the same image yourself instead of pulling it from GHCR:

```bash
docker build -f tools/MigrateToInfinidysk/Dockerfile -t nzbdav2-migrate-infinidysk:local \
  tools/MigrateToInfinidysk
docker run --rm \
  -v /path/to/nzbdav2/config:/source:ro \
  -v /path/to/infinidysk-config:/target \
  nzbdav2-migrate-infinidysk:local \
  --source /source --target /target
```
