using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Makes --apply atomic as a whole. Ordering (deliberately archive-then-DB, not DB-then-archive):
///   1. Validate the final archive destination up front (not a directory, parent exists).
///   2. Write the archive to a temp file in the same directory as the final path.
///   3. Publish it: atomically rename the temp file onto the final --archive-path.
///   4. Only now, with the archive durably in place, apply the DB transaction and commit.
/// Committing the DB LAST means the DB commit is the actual point of no return, and every step
/// before it (including the archive publish) is either fully done or fully rolled back before
/// that point - so a failure anywhere in steps 1-3 leaves the target DB with zero writes, full
/// stop. (Round 2 shipped the reverse order - DB commit before archive publish - which left a
/// window where a failing rename came after a successful commit; round 3 mitigated that with a
/// pre-write directory-existence check, but that check couldn't catch every publish failure,
/// e.g. a permissions error on an existing file. Publishing first removes the window entirely
/// rather than trying to predict every way the publish step could fail.)
/// If the DB apply itself fails (after the archive was already published), the published
/// archive is deleted on a best-effort basis so a failed apply doesn't leave a stray archive
/// with no corresponding DB state - the DB rollback (via SqliteTransaction's own dispose
/// semantics) is what actually matters for correctness; the archive cleanup is tidiness on top.
/// </summary>
public static class MigrationApplier
{
    public static void Apply(SqliteConnection targetConn, MigrationResult result, string archivePath)
    {
        // Step 1: validate the FINAL destination is actually renameable to, before touching
        // anything. Reproduced bug: --archive-path pointed at an existing directory wrote fine
        // to the temp path (a sibling file, not inside that directory) - only the final
        // File.Move failed. Catching the obvious cases here means most bad --archive-path
        // values never even reach the publish step below.
        ValidateFinalDestination(archivePath);

        var tempArchivePath = archivePath + $".tmp-{Guid.NewGuid():N}";

        // Step 2: archive to a temp file. If this throws, nothing else has happened yet - the
        // target DB transaction hasn't even started, and the final --archive-path is untouched.
        JsonArchiveWriter.Write(tempArchivePath, result.Archive);

        // Step 3: publish the archive to its final location BEFORE the DB commit, not after.
        // If this throws (permissions, read-only destination, disk full, or any other publish
        // failure ValidateFinalDestination's cheap checks didn't catch), the temp file is
        // cleaned up and we return with the target DB completely untouched - there is no DB
        // transaction open yet at this point.
        try
        {
            File.Move(tempArchivePath, archivePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempArchivePath);
            throw;
        }

        // Step 4: only now, with the archive durably published, apply the DB writes in one
        // transaction. If this throws, the transaction rolls back (SqliteTransaction disposed
        // without Commit()), and we best-effort delete the archive we just published so a
        // failed apply doesn't leave an orphaned archive with no matching DB state.
        try
        {
            SqliteTargetWriter.Apply(targetConn, result);
        }
        catch
        {
            TryDelete(archivePath);
            throw;
        }
    }

    private static void ValidateFinalDestination(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);

        if (Directory.Exists(full))
        {
            throw new IOException(
                $"--archive-path '{archivePath}' is an existing directory, not a file. Refusing to apply - " +
                "nothing was written. Choose a file path for --archive-path.");
        }

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"--archive-path's parent directory does not exist: {parent}. Refusing to apply - " +
                "nothing was written.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best-effort cleanup only.
        }
    }
}
