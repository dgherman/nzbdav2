using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Mapping;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk;

public static class Program
{
    public static int Main(string[] args)
    {
        string? sourceConfigPath = null;
        string? targetConfigPath = null;
        string? adminUsername = null;
        string? archivePath = null;
        var apply = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source": sourceConfigPath = args[++i]; break;
                case "--target": targetConfigPath = args[++i]; break;
                case "--admin-username": adminUsername = args[++i]; break;
                case "--archive-path": archivePath = args[++i]; break;
                case "--apply": apply = true; break;
                case "--dry-run": apply = false; break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (sourceConfigPath == null || targetConfigPath == null)
        {
            Console.Error.WriteLine(
                "Usage: MigrateToInfinidysk --source <nzbdav2 CONFIG_PATH> --target <infinidysk CONFIG_PATH> " +
                "[--admin-username <name>] [--archive-path <file.json>] [--apply]\n" +
                "Defaults to --dry-run (reports only, writes nothing) until --apply is passed.");
            return 2;
        }

        var sourceDbPath = Path.Join(sourceConfigPath, "db.sqlite");
        var targetDbPath = Path.Join(targetConfigPath, "db.sqlite");
        archivePath ??= Path.Join(apply ? targetConfigPath : sourceConfigPath, "nzbdav2-migration-archive.json");

        if (!File.Exists(sourceDbPath))
        {
            Console.Error.WriteLine($"Source database not found: {sourceDbPath}");
            return 1;
        }
        if (!File.Exists(targetDbPath))
        {
            Console.Error.WriteLine(
                $"Target database not found: {targetDbPath}\n" +
                "Start infinidysk once against this (otherwise empty) config volume first, so it finishes its " +
                "own startup migration, then re-run this tool.");
            return 1;
        }

        // Canonicalize + validate before opening anything, so a bad --archive-path (e.g.
        // accidentally pointed at the source or target db.sqlite) is rejected before any
        // connection is even opened, let alone any write attempted.
        var archivePathCheck = ArchivePathGuard.Check(archivePath, sourceDbPath, targetDbPath);
        if (!archivePathCheck.IsValid)
        {
            Console.Error.WriteLine(archivePathCheck.ErrorMessage);
            return 1;
        }

        // Source is opened read-only: this tool must never modify the nzbdav2 database.
        // SqliteConnectionStringBuilder (rather than string interpolation) so a data source
        // path containing ';' or '"' can't alter connection-string parsing.
        using var sourceConn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        sourceConn.Open();
        using var targetConn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = targetDbPath,
            Mode = apply ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
        }.ToString());
        targetConn.Open();

        var sourceMigrations = SqliteSourceReader.ReadMigrationHistory(sourceConn);
        var sourceCheck = SchemaGuard.CheckSource(sourceMigrations);
        if (!sourceCheck.IsValid)
        {
            Console.Error.WriteLine(sourceCheck.ErrorMessage);
            return 1;
        }

        var targetMigrations = ReadTargetMigrationHistory(targetConn);
        var targetMigrationCheck = SchemaGuard.CheckTarget(targetMigrations);
        if (!targetMigrationCheck.IsValid)
        {
            Console.Error.WriteLine(targetMigrationCheck.ErrorMessage);
            return 1;
        }

        // Authoritative check: actual column introspection, not just migration-history rows
        // (which a hand-built or tampered database could satisfy without the real schema).
        var targetSchemaCheck = SchemaGuard.CheckTargetSchema(targetConn);
        if (!targetSchemaCheck.IsValid)
        {
            Console.Error.WriteLine(targetSchemaCheck.ErrorMessage);
            return 1;
        }

        // StreamingMigrator (not the fixture-oriented Migrator.Run - see its class doc comment)
        // streams every large table row by row instead of materializing the whole source
        // database, the whole mapped target, and the whole archive simultaneously - fixes an OOM
        // reported on real-world databases (thousands of AnalysisHistoryItems/BandwidthSamples
        // rows, hundreds of QueueNzbContents rows carrying full NZB XML text) that could crash
        // --apply even under an 8GB container memory limit. It also drives the archive-then-
        // commit ordering directly (same atomicity guarantee MigrationApplier provided), so
        // --apply no longer goes through a separate MigrationApplier.Apply call.
        var result = StreamingMigrator.Run(sourceConn, apply ? targetConn : null, apply ? archivePath : null, new MigrationOptions(adminUsername));

        DryRunReport.Print(result, apply);

        if (!result.Success)
            return 1;

        if (!apply)
        {
            Console.WriteLine("\nDry run only - no changes were written. Re-run with --apply to write them.");
            return 0;
        }

        Console.WriteLine($"\nApplied. Archive written to: {archivePath}");
        return 0;
    }

    private static IReadOnlyList<string> ReadTargetMigrationHistory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
