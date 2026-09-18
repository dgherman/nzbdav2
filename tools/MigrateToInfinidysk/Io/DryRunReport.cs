using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Io;

public static class DryRunReport
{
    public static void Print(MigrationResult result, bool applying)
    {
        Console.WriteLine(applying ? "=== Applying nzbdav2 -> infinidysk migration ===" : "=== Dry run: nzbdav2 -> infinidysk migration ===");

        if (!result.Success)
        {
            Console.WriteLine("\nFAILED - refusing to write anything:");
            foreach (var error in result.Errors)
                Console.WriteLine($"  - {error}");
            return;
        }

        Console.WriteLine("\nPer-table counts (copied / skipped / archived):");
        foreach (var (table, counts) in result.Counts.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {table,-28} {counts.Copied,6} / {counts.Skipped,6} / {counts.Archived,6}");

        if (result.Infos is { Count: > 0 })
        {
            Console.WriteLine("\nInfo:");
            foreach (var info in result.Infos)
                Console.WriteLine($"  - {info}");
        }

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine("\nWarnings:");
            foreach (var warning in result.Warnings)
                Console.WriteLine($"  - {warning}");
        }
    }
}
