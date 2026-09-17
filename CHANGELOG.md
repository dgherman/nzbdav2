# Changelog

## Unreleased

### Added

- `tools/MigrateToInfinidysk`: an unofficial, community migration tool for moving a nzbdav2
  installation onto [infinidysk](https://github.com/), the actively-maintained successor to
  the unmaintained `nzbdav-dev/nzbdav` upstream. Reads a nzbdav2 `db.sqlite` (read-only) and
  writes the equivalent rows into an already-initialized infinidysk database, with a
  `--dry-run` mode (default) that reports exactly what will be copied/mapped/skipped before
  anything is written, and a JSON sidecar archive for data with no infinidysk equivalent
  (obfuscated media, provider/bandwidth diagnostics history, analysis history, local links, and
  a handful of nzbdav2-only fields on shared tables). See `MIGRATING_TO_INFINIDYSK.md` for the
  full procedure and compatibility reference. Built test-first
  (`tools/MigrateToInfinidysk.Tests`), including an end-to-end test against real fixture SQLite
  databases exercising the tool's real constraints (obfuscation-key detection, multi-admin
  conflict resolution, `QueueItem.SortOrder` backfill).
- `tools/MigrateToInfinidysk/Dockerfile`: standalone self-contained image for the migration tool
  above, published to `ghcr.io/dgherman/nzbdav2-migrate-infinidysk` by
  `.github/workflows/docker-publish.yml` (same tagging scheme as the main nzbdav2 image). No
  .NET SDK or repo checkout needed to run it - `docker run` it the same way you already run
  nzbdav2/infinidysk, bind-mounting the source/target config directories. See
  `MIGRATING_TO_INFINIDYSK.md`.
