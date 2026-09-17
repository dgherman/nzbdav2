using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

/// <summary>
/// Makes --apply atomic as a whole: the archive is written to a temp file first (a failure
/// there touches neither the temp file nor the target DB), then the target DB transaction is
/// applied (a failure there rolls the transaction back via SqliteTransaction's own dispose
/// semantics and deletes the temp archive), and only once both have succeeded is the temp
/// archive atomically renamed into its final place. There is no window where the target DB is
/// committed but the archive is missing, or vice versa.
/// </summary>
public static class MigrationApplier
{
    public static void Apply(SqliteConnection targetConn, MigrationResult result, string archivePath)
    {
        // Step 0: validate the FINAL destination is actually renameable to, before touching
        // anything. Reproduced bug: --archive-path pointed at an existing directory wrote fine
        // to the temp path (a sibling file, not inside that directory) and the DB transaction
        // committed successfully - only the final File.Move failed, after the point of no
        // return. Catching this up front means a bad --archive-path can never leave a
        // committed-DB/missing-archive partial state.
        ValidateFinalDestination(archivePath);

        var tempArchivePath = archivePath + $".tmp-{Guid.NewGuid():N}";

        // Step 1: archive to a temp file. If this throws, nothing else has happened yet - the
        // target DB transaction hasn't even started.
        JsonArchiveWriter.Write(tempArchivePath, result.Archive);

        // Step 2: apply the DB writes in one transaction. If this throws, the transaction rolls
        // back (SqliteTransaction disposed without Commit()), and we clean up the temp archive
        // so a failed apply leaves zero trace.
        try
        {
            SqliteTargetWriter.Apply(targetConn, result);
        }
        catch
        {
            TryDelete(tempArchivePath);
            throw;
        }

        // Step 3: only now, with the DB transaction already committed, finalize the archive.
        File.Move(tempArchivePath, archivePath, overwrite: true);
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
            // best-effort cleanup only - the DB rollback is what actually matters here.
        }
    }
}
