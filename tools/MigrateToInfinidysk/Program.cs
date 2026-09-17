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

        // Source is opened read-only: this tool must never modify the nzbdav2 database.
        using var sourceConn = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly");
        sourceConn.Open();
        using var targetConn = new SqliteConnection($"Data Source={targetDbPath};Mode={(apply ? "ReadWrite" : "ReadOnly")}");
        targetConn.Open();

        var sourceMigrations = SqliteSourceReader.ReadMigrationHistory(sourceConn);
        var sourceCheck = SchemaGuard.CheckSource(sourceMigrations);
        if (!sourceCheck.IsValid)
        {
            Console.Error.WriteLine(sourceCheck.ErrorMessage);
            return 1;
        }

        var targetMigrations = ReadTargetMigrationHistory(targetConn);
        var targetCheck = SchemaGuard.CheckTarget(targetMigrations);
        if (!targetCheck.IsValid)
        {
            Console.Error.WriteLine(targetCheck.ErrorMessage);
            return 1;
        }

        var snapshot = SqliteSourceReader.Read(sourceConn);
        var result = Migrator.Run(snapshot, new MigrationOptions(adminUsername));

        DryRunReport.Print(result, apply);

        if (!result.Success)
            return 1;

        if (!apply)
        {
            Console.WriteLine("\nDry run only - no changes were written. Re-run with --apply to write them.");
            return 0;
        }

        SqliteTargetWriter.Apply(targetConn, result);
        JsonArchiveWriter.Write(archivePath, result.Archive);
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
