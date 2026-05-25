using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// LEGACY (remove in follow-up release): one-time, idempotent conversion of pre-v0.8.0
/// DavRarFile rows into DavMultipartFile rows. RarFile items are no longer created; this
/// migrates existing user data forward so it uses the single multipart streaming model and
/// benefits from per-segment seek offsets. Safe to run on every startup — converts only rows
/// that still exist and returns the number of items converted.
/// </summary>
public static class LegacyRarFileMigration
{
    public static async Task<int> RunAsync(DavDatabaseContext ctx, CancellationToken ct = default)
    {
        var rarFiles = await ctx.RarFiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (rarFiles.Count == 0) return 0;

        Log.Warning("[LegacyRarFileMigration] Converting {Count} legacy DavRarFile rows to DavMultipartFile...", rarFiles.Count);

        // Step 1: flip the DavItem type RarFile(4) -> MultipartFile(6) FIRST. DavItem.Type is init-only
        // on the model, so update via SQL (values are compile-time enum constants, no user input).
        // Doing this before deleting the rar rows means an interrupted run always leaves the rar rows as
        // the durable "work remaining" marker — never a deleted rar row with a stale RarFile-typed item.
        await ctx.Database.ExecuteSqlRawAsync(
            $"UPDATE DavItems SET Type = {(int)DavItem.ItemType.MultipartFile} WHERE Type = {(int)DavItem.ItemType.RarFile};",
            ct).ConfigureAwait(false);

        // Step 2: create the replacement DavMultipartFile rows and remove the legacy rar rows.
        var converted = 0;
        foreach (var rarFile in rarFiles)
        {
            var itemExists = await ctx.Items.AnyAsync(x => x.Id == rarFile.Id, ct).ConfigureAwait(false);

            // Create the replacement DavMultipartFile (skip if one already exists from a partial prior run).
            if (itemExists && !await ctx.MultipartFiles.AnyAsync(x => x.Id == rarFile.Id, ct).ConfigureAwait(false))
            {
                ctx.MultipartFiles.Add(new DavMultipartFile
                {
                    Id = rarFile.Id,
                    Metadata = rarFile.ToDavMultipartFileMeta(),
                });
            }

            // Remove the legacy rar row (also drops orphan rar rows whose DavItem is gone).
            var tracked = await ctx.RarFiles.FirstAsync(x => x.Id == rarFile.Id, ct).ConfigureAwait(false);
            ctx.RarFiles.Remove(tracked);
            if (itemExists) converted++;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        Log.Warning("[LegacyRarFileMigration] Converted {Count} DavRarFile rows.", converted);
        return converted;
    }
}
