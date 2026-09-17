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

2. **Start infinidysk fresh against a new, empty config volume** so it runs its own startup
   migrations and creates a fully-migrated `db.sqlite`. Do **not** point it at the nzbdav2
   volume yet.
   ```bash
   docker run --rm -v infinidysk-config:/config ghcr.io/<infinidysk-image> # let it finish startup, then stop it
   docker stop <that container>
   ```

3. **Stop infinidysk** once its startup migration has finished (check its logs for the
   migration-complete message, or that `db.sqlite` exists and has grown past a few KB).

4. **Run the migration tool in `--dry-run` first** (this is the default - it refuses to write
   anything until you pass `--apply`):
   ```bash
   dotnet run --project tools/MigrateToInfinidysk -- \
     --source /path/to/nzbdav2/config \
     --target /path/to/infinidysk-config
   ```
   Read the per-table report carefully. It shows exactly what will be copied, mapped, or
   skipped, with row counts, and flags:
   - any `DavMultipartFiles`/`DavRarFiles` rows with an obfuscation key, or with no recorded key
     at all (nzbdav2 gives no reliable way to prove a given row's content definitely isn't
     obfuscated - see "What is NOT preserved" below), that will be excluded entirely - both the
     file's metadata and its `DavItems` entry, so it simply won't appear in the target library,
   - any `ConfigItems` keys infinidysk doesn't recognize (notably nzbdav2's discrete
     `usenet.host`/`port`/`use-ssl`/`connections`/`user`/`pass` keys - infinidysk consolidates
     usenet provider configuration into a single `usenet.providers` key with a different shape,
     so **you must reconfigure your usenet provider(s) manually in infinidysk's UI after
     migrating** - this is not done for you),
   - a multi-admin conflict if your nzbdav2 database has more than one `Accounts` row with
     `Type = Admin` (infinidysk allows only one; re-run with `--admin-username <name>` to
     choose which one to keep).

5. **Apply it** once the dry-run report looks right:
   ```bash
   dotnet run --project tools/MigrateToInfinidysk -- \
     --source /path/to/nzbdav2/config \
     --target /path/to/infinidysk-config \
     --apply
   ```
   This writes into the infinidysk database only, and writes a
   `nzbdav2-migration-archive.json` sidecar (next to infinidysk's `db.sqlite` by default,
   override with `--archive-path`) containing everything that has no infinidysk equivalent -
   see "What is NOT preserved" below.

6. **Start infinidysk against the now-populated config volume.**

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

- **Obfuscated/deobfuscated media playback state.** infinidysk's `DavMultipartFile.Meta` has no
  field for RAR obfuscation keys. Any `DavMultipartFiles`/`DavRarFiles` row where nzbdav2
  recorded a non-null obfuscation key is not imported, since there's nowhere in infinidysk's
  schema to put that key. Rows with **no** recorded key are conservatively excluded too:
  nzbdav2 provides no way to prove from the database alone that a given row's content isn't
  obfuscated (`RarAggregator`, `MultipartMkvProcessor`, and `SevenZipProcessor` all produce
  identically-shaped rows regardless of origin, and nzbdav2's `RarDeobfuscationStream` wraps
  every multipart file read and falls back to content-sniffed default-key detection whenever no
  key was recorded). **The affected file's entire entry - both its metadata and its place in the
  file listing - is left out of the target library**, not just its playback data; it will not
  appear at all until you manually re-download or re-process it under infinidysk. Every skipped
  file's ID, recorded key (if any), and full source metadata are written to the JSON archive so
  you can identify and recover them.
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
  numbers in the NZB, used as a retry path when a primary article is missing). Unlike the
  byte-range optimization above, this is **not** re-derivable data - it's a literal list of
  alternate article IDs recorded when the NZB was originally queued. infinidysk's `DavNzbFiles`
  table has no SQL column for it (only `Id`/`SegmentIds`), so it cannot be written into the live
  target database; it is archived to the JSON sidecar instead, aligned by segment index, so it
  isn't silently lost. If you rely on this retry path, check the archive for affected files.
- **Legacy `usenet.host`/`port`/`use-ssl`/`connections`/`user`/`pass` config keys.** infinidysk
  replaced these with a single `usenet.providers` key of a different shape. You must
  reconfigure your usenet provider(s) manually in infinidysk after migrating.

## Compatibility reference

| nzbdav2 table | Status | Notes |
|---|---|---|
| `DavItems` | Mapped | Legacy `Type` enum split into `Type`+`SubType`; fixed root IDs merged by ID, not duplicated |
| `DavNzbFiles` | Mapped | `SegmentIds` copied; byte-range optimization left for infinidysk to lazily re-derive; `SegmentFallbacks` (not re-derivable) archived to the JSON sidecar - no SQL column exists for it |
| `DavMultipartFiles` / `DavRarFiles` | Mapped, with skips | Rows with an obfuscation key, or with no recorded key at all (nzbdav2 gives us no reliable way to tell those apart from the database alone), are skipped along with their `DavItems` row and archived with full recoverable payload; `DavRarFiles` rows are otherwise converted into `DavMultipartFiles` the same way nzbdav2 itself converts them |
| `LocalLinks` | **Incompatible** | Archived to JSON sidecar only |
| `QueueItems` | Mapped | `SortOrder` backfilled with infinidysk's own `ROW_NUMBER() OVER (PARTITION BY Priority ORDER BY CreatedAt, Id) * 1024` formula |
| `QueueNzbContents` | Compatible | Copied unchanged |
| `HistoryItems` | Mapped | Shared columns copied; nzbdav2-only fields archived (see above) |
| `AnalysisHistoryItems`, `BandwidthSamples`, `MissingArticleEvents`, `MissingArticleSummaries`, `NzbProviderStats`, `ProviderBenchmarkResults` | **Incompatible** | Archived to JSON sidecar only |
| `ConfigItems` | Filtered | Only keys infinidysk actually reads are copied; others skipped and reported |
| `Accounts` | Mapped, with conflict check | infinidysk allows only one Admin account; multiple require `--admin-username` |
| `HealthCheckStats` | Compatible | Copied unchanged |
| `HealthCheckResults` | Mapped | `Operation` field archived; rest copied |
