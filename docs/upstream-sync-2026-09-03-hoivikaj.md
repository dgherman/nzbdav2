# Third-Party Fork Sync — 2026-09-03

Source: [https://github.com/nzbdav/nzbdav](https://github.com/nzbdav/nzbdav), the third-party `nzbdav-FORK-hoivikaj` checkout (`fork-hoivikaj` remote). This repository is not canonical upstream.

## Adopted

The following logic was ported to the vendored SharpCompress copy rather than cherry-picked because the fork's SharpCompress lineage and file layout differ:

*   `GetPrimaryCoder` selects the first non-AES coder so an encrypted coder chain is classified by its payload compression method.
*   `GetCompression` returns `CompressionType.None` when an entry has no stream or folder.
*   `IsEncrypted` requires a non-null folder before searching its coder list, avoiding the nullable-lifted `null != -1` result.
*   `SevenZipArchiveEntryExtensions.GetCompressionType` handles `InvalidFormatException` like `NotImplementedException` for compatibility with this repository's vendored SharpCompress snapshot.

Attribution: fork commits `fd1bd62e` and `4c4a3d92`. The port retains this repository's vendored SharpCompress types and APIs.

## Skipped

*   The `NzbDav.SharpCompress` package swap was skipped because this fix only needs the coder-selection and null-guard logic; replacing the vendored library would be a broad dependency migration.
*   `LazyRarProcessor`, `LazyRarResolver`, and related `LazyRar*` work were skipped because they are unrelated to 7z AES compression metadata.
*   The fork's blob-store changes were skipped because they change persistence architecture and do not address this import failure.
*   The #1040 asynchronous `EntriesAsync` refactor was skipped because it is independent of the synchronous metadata failure and would expand the scope substantially.
