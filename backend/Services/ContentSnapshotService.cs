using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;
using ZstdSharp;

namespace NzbWebDAV.Services;

/// <summary>
/// Persists a snapshot of the /content subtree to disk so it can be restored
/// if database rows go missing (e.g., after a crash or corruption).
/// Intentional deletes are reflected in the snapshot — deleted items stay deleted.
/// </summary>
public class ContentSnapshotService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static string ConfigPath => DavDatabaseContext.ConfigPath;
    private static string SnapshotPath => Path.Combine(ConfigPath, "content-snapshot.json.zst");
    private static string BackupPath => Path.Combine(ConfigPath, "content-snapshot.backup.json.zst");

    // Clean up old uncompressed snapshots from previous versions
    private static string LegacySnapshotPath => Path.Combine(ConfigPath, "content-snapshot.json");
    private static string LegacyBackupPath => Path.Combine(ConfigPath, "content-snapshot.backup.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Warning("[ContentSnapshot] Service starting...");

        // Run recovery check on startup before anything else
        try
        {
            await RunRecoveryCheckAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ContentSnapshot] Recovery check failed on startup");
        }

        // Take initial snapshot after a short delay
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SaveSnapshotAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ContentSnapshot] Failed to save snapshot");
            }

            // Save snapshot every 5 minutes
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks if /content is empty/partial and restores from snapshot if needed.
    /// </summary>
    private async Task RunRecoveryCheckAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        // Count current /content children
        var contentChildCount = await db.Items
            .Where(x => x.ParentId == DavItem.ContentFolder.Id)
            .CountAsync(ct).ConfigureAwait(false);

        if (contentChildCount > 0)
        {
            Log.Warning("[ContentSnapshot] /content has {Count} children — no recovery needed", contentChildCount);
            return;
        }

        // /content is empty — try to restore from snapshot
        Log.Warning("[ContentSnapshot] /content is EMPTY — attempting recovery from snapshot");

        var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
        if (snapshot == null)
        {
            Log.Warning("[ContentSnapshot] No valid snapshot found — cannot recover");
            return;
        }

        if (snapshot.Items.Count == 0)
        {
            Log.Information("[ContentSnapshot] Snapshot is also empty — nothing to recover (likely intentional)");
            return;
        }

        await RestoreFromSnapshotAsync(db, snapshot, ct).ConfigureAwait(false);
    }

    private async Task RestoreFromSnapshotAsync(DavDatabaseContext db, ContentSnapshot snapshot, CancellationToken ct)
    {
        Log.Warning("[ContentSnapshot] Restoring {ItemCount} items, {NzbCount} NZB files, {RarCount} RAR files, {MultipartCount} multipart files from snapshot",
            snapshot.Items.Count, snapshot.NzbFiles.Count, snapshot.RarFiles.Count, snapshot.MultipartFiles.Count);

        // Get IDs of items that already exist in the database
        var existingIds = await db.Items
            .Where(x => snapshot.Items.Select(i => i.Id).Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(ct).ConfigureAwait(false);

        var restoredItems = 0;
        var restoredNzb = 0;
        var restoredRar = 0;
        var restoredMultipart = 0;

        // Restore DavItems that are missing (in order: directories first, then files)
        var orderedItems = snapshot.Items
            .Where(x => !existingIds.Contains(x.Id))
            .OrderBy(x => x.Path.Count(c => c == '/'))
            .ToList();

        foreach (var item in orderedItems)
        {
            db.Items.Add(item);
            restoredItems++;
        }

        // Restore NzbFile rows
        var existingNzbIds = await db.NzbFiles
            .Where(x => snapshot.NzbFiles.Select(n => n.Id).Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(ct).ConfigureAwait(false);

        foreach (var nzbFile in snapshot.NzbFiles.Where(x => !existingNzbIds.Contains(x.Id)))
        {
            db.NzbFiles.Add(nzbFile);
            restoredNzb++;
        }

        // Restore RarFile rows
        var existingRarIds = await db.RarFiles
            .Where(x => snapshot.RarFiles.Select(r => r.Id).Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(ct).ConfigureAwait(false);

        foreach (var rarFile in snapshot.RarFiles.Where(x => !existingRarIds.Contains(x.Id)))
        {
            db.RarFiles.Add(rarFile);
            restoredRar++;
        }

        // Restore MultipartFile rows
        var existingMultipartIds = await db.MultipartFiles
            .Where(x => snapshot.MultipartFiles.Select(m => m.Id).Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(ct).ConfigureAwait(false);

        foreach (var multipartFile in snapshot.MultipartFiles.Where(x => !existingMultipartIds.Contains(x.Id)))
        {
            db.MultipartFiles.Add(multipartFile);
            restoredMultipart++;
        }

        if (restoredItems == 0)
        {
            Log.Information("[ContentSnapshot] All snapshot items already exist in database — no recovery needed");
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        Log.Warning("[ContentSnapshot] RECOVERY COMPLETE: Restored {Items} items, {Nzb} NZB files, {Rar} RAR files, {Multipart} multipart files",
            restoredItems, restoredNzb, restoredRar, restoredMultipart);
    }

    /// <summary>
    /// Saves a snapshot of all /content items and their file metadata.
    /// </summary>
    public async Task SaveSnapshotAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        // Get all items under /content (path starts with /content/)
        var allItems = await db.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .ToListAsync(ct).ConfigureAwait(false);

        if (allItems.Count == 0)
        {
            // Don't overwrite a good snapshot with an empty one unless this is
            // the initial state (no snapshot exists yet)
            if (File.Exists(SnapshotPath))
            {
                Log.Debug("[ContentSnapshot] /content is empty but snapshot exists — not overwriting");
                return;
            }
        }

        var itemIds = allItems.Select(x => x.Id).ToHashSet();

        // Get associated file metadata
        var nzbFiles = await db.NzbFiles
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var rarFiles = await db.RarFiles
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var multipartFiles = await db.MultipartFiles
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var snapshot = new ContentSnapshot
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Items = allItems,
            NzbFiles = nzbFiles,
            RarFiles = rarFiles,
            MultipartFiles = multipartFiles,
        };

        // Write compressed to temp file, then atomically move
        var tempPath = SnapshotPath + ".tmp";
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

        using (var compressor = new Compressor(1))
        {
            var compressed = compressor.Wrap(jsonBytes);

            // Rotate: current → backup before writing new
            if (File.Exists(SnapshotPath))
            {
                File.Copy(SnapshotPath, BackupPath, overwrite: true);
            }

            await File.WriteAllBytesAsync(tempPath, compressed.ToArray(), ct).ConfigureAwait(false);
            File.Move(tempPath, SnapshotPath, overwrite: true);
        }

        // Clean up legacy uncompressed snapshots
        if (File.Exists(LegacySnapshotPath)) File.Delete(LegacySnapshotPath);
        if (File.Exists(LegacyBackupPath)) File.Delete(LegacyBackupPath);

        var fileSizeMb = new FileInfo(SnapshotPath).Length / (1024.0 * 1024.0);
        Log.Warning("[ContentSnapshot] Saved snapshot: {Items} items, {Nzb} NZB, {Rar} RAR, {Multipart} multipart ({SizeMb:F1} MB compressed)",
            allItems.Count, nzbFiles.Count, rarFiles.Count, multipartFiles.Count, fileSizeMb);
    }

    /// <summary>
    /// Loads snapshot from primary file, falls back to backup if primary is corrupt.
    /// </summary>
    private static async Task<ContentSnapshot?> LoadSnapshotAsync()
    {
        var snapshot = await TryLoadSnapshotFile(SnapshotPath).ConfigureAwait(false);
        if (snapshot != null) return snapshot;

        Log.Warning("[ContentSnapshot] Primary snapshot missing or corrupt — trying backup");
        return await TryLoadSnapshotFile(BackupPath).ConfigureAwait(false);
    }

    private static async Task<ContentSnapshot?> TryLoadSnapshotFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var compressed = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

            using var decompressor = new Decompressor();
            var jsonBytes = decompressor.Unwrap(compressed);
            var snapshot = JsonSerializer.Deserialize<ContentSnapshot>(jsonBytes, JsonOptions);

            if (snapshot?.Items == null)
            {
                Log.Warning("[ContentSnapshot] Snapshot at {Path} is invalid (null items)", path);
                return null;
            }

            Log.Warning("[ContentSnapshot] Loaded snapshot from {Path}: {Items} items, created {CreatedAt}",
                path, snapshot.Items.Count, snapshot.CreatedAt);
            return snapshot;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ContentSnapshot] Failed to load snapshot from {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Snapshot data model — serialized to JSON on disk.
    /// </summary>
    private class ContentSnapshot
    {
        public DateTimeOffset CreatedAt { get; set; }
        public List<DavItem> Items { get; set; } = [];
        public List<DavNzbFile> NzbFiles { get; set; } = [];
        public List<DavRarFile> RarFiles { get; set; } = [];
        public List<DavMultipartFile> MultipartFiles { get; set; } = [];
    }
}
