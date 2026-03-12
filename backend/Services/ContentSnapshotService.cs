using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;
using ZstdSharp;

namespace NzbWebDAV.Services;

/// <summary>
/// Persists a snapshot of the /content subtree to disk so it can be restored
/// if database rows go missing (e.g., after a crash or corruption).
/// Uses raw SQL to read/write DB values without EF value converter expansion.
/// </summary>
public class ContentSnapshotService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static string ConfigPath => DavDatabaseContext.ConfigPath;
    private static string SnapshotPath => Path.Combine(ConfigPath, "content-snapshot.json.zst");
    private static string BackupPath => Path.Combine(ConfigPath, "content-snapshot.backup.json.zst");
    private static string LegacySnapshotPath => Path.Combine(ConfigPath, "content-snapshot.json");
    private static string LegacyBackupPath => Path.Combine(ConfigPath, "content-snapshot.backup.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Warning("[ContentSnapshot] Service starting...");

        try
        {
            await RunRecoveryCheckAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ContentSnapshot] Recovery check failed on startup");
        }

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

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunRecoveryCheckAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        var contentChildCount = await db.Items
            .Where(x => x.ParentId == DavItem.ContentFolder.Id)
            .CountAsync(ct).ConfigureAwait(false);

        if (contentChildCount > 0)
        {
            Log.Warning("[ContentSnapshot] /content has {Count} children — no recovery needed", contentChildCount);
            return;
        }

        Log.Warning("[ContentSnapshot] /content is EMPTY — attempting recovery from snapshot");

        var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
        if (snapshot == null)
        {
            Log.Warning("[ContentSnapshot] No valid snapshot found — cannot recover");
            return;
        }

        if (snapshot.Items.Count == 0)
        {
            Log.Warning("[ContentSnapshot] Snapshot is also empty — nothing to recover (likely intentional)");
            return;
        }

        await RestoreFromSnapshotAsync(db, snapshot, ct).ConfigureAwait(false);
    }

    private async Task RestoreFromSnapshotAsync(DavDatabaseContext db, ContentSnapshot snapshot, CancellationToken ct)
    {
        Log.Warning("[ContentSnapshot] Restoring {ItemCount} items, {NzbCount} NZB files, {RarCount} RAR files, {MultipartCount} multipart files from snapshot",
            snapshot.Items.Count, snapshot.NzbFiles.Count, snapshot.RarFiles.Count, snapshot.MultipartFiles.Count);

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);

        // Get existing IDs to avoid duplicates
        var existingItemIds = new HashSet<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM DavItems WHERE Path LIKE '/content/%'";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existingItemIds.Add(reader.GetString(0));
        }

        var restoredItems = 0;
        var restoredFiles = 0;

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Restore DavItems (directories first by path depth)
        foreach (var item in snapshot.Items.OrderBy(x => x.Path.Count(c => c == '/')))
        {
            if (existingItemIds.Contains(item.Id.ToString())) continue;

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (System.Data.Common.DbTransaction)tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO DavItems
                (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, Path, ReleaseDate, LastHealthCheck, NextHealthCheck, MediaInfo, IsCorrupted, CorruptionReason)
                VALUES (@id, @idPrefix, @createdAt, @parentId, @name, @fileSize, @type, @path, @releaseDate, @lastHealthCheck, @nextHealthCheck, @mediaInfo, @isCorrupted, @corruptionReason)";
            AddParam(cmd, "@id", item.Id);
            AddParam(cmd, "@idPrefix", item.IdPrefix);
            AddParam(cmd, "@createdAt", item.CreatedAt);
            AddParam(cmd, "@parentId", item.ParentId ?? (object)DBNull.Value);
            AddParam(cmd, "@name", item.Name);
            AddParam(cmd, "@fileSize", item.FileSize ?? (object)DBNull.Value);
            AddParam(cmd, "@type", item.Type);
            AddParam(cmd, "@path", item.Path);
            AddParam(cmd, "@releaseDate", item.ReleaseDate ?? (object)DBNull.Value);
            AddParam(cmd, "@lastHealthCheck", item.LastHealthCheck ?? (object)DBNull.Value);
            AddParam(cmd, "@nextHealthCheck", item.NextHealthCheck ?? (object)DBNull.Value);
            AddParam(cmd, "@mediaInfo", item.MediaInfo ?? (object)DBNull.Value);
            AddParam(cmd, "@isCorrupted", item.IsCorrupted ? 1 : 0);
            AddParam(cmd, "@corruptionReason", item.CorruptionReason ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            restoredItems++;
        }

        // Restore NzbFiles (raw column values)
        foreach (var nzb in snapshot.NzbFiles)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (System.Data.Common.DbTransaction)tx;
            cmd.CommandText = "INSERT OR IGNORE INTO DavNzbFiles (Id, SegmentIds, SegmentSizes, SegmentFallbacks) VALUES (@id, @segmentIds, @segmentSizes, @segmentFallbacks)";
            AddParam(cmd, "@id", nzb.Id);
            AddParam(cmd, "@segmentIds", nzb.SegmentIds);
            AddParam(cmd, "@segmentSizes", nzb.SegmentSizes ?? (object)DBNull.Value);
            AddParam(cmd, "@segmentFallbacks", nzb.SegmentFallbacks ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            restoredFiles++;
        }

        // Restore RarFiles (raw column values)
        foreach (var rar in snapshot.RarFiles)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (System.Data.Common.DbTransaction)tx;
            cmd.CommandText = "INSERT OR IGNORE INTO DavRarFiles (Id, RarParts) VALUES (@id, @rarParts)";
            AddParam(cmd, "@id", rar.Id);
            AddParam(cmd, "@rarParts", rar.RarParts);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            restoredFiles++;
        }

        // Restore MultipartFiles (raw column values)
        foreach (var mp in snapshot.MultipartFiles)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (System.Data.Common.DbTransaction)tx;
            cmd.CommandText = "INSERT OR IGNORE INTO DavMultipartFiles (Id, Metadata) VALUES (@id, @metadata)";
            AddParam(cmd, "@id", mp.Id);
            AddParam(cmd, "@metadata", mp.Metadata);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            restoredFiles++;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        Log.Warning("[ContentSnapshot] RECOVERY COMPLETE: Restored {Items} items, {Files} file metadata rows",
            restoredItems, restoredFiles);
    }

    public async Task SaveSnapshotAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);

        // Get all /content item IDs first
        var allItems = await db.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith("/content/"))
            .ToListAsync(ct).ConfigureAwait(false);

        if (allItems.Count == 0 && File.Exists(SnapshotPath))
        {
            Log.Debug("[ContentSnapshot] /content is empty but snapshot exists — not overwriting");
            return;
        }

        var itemIds = allItems.Select(x => x.Id.ToString().ToUpperInvariant()).ToHashSet();

        // Read file metadata using raw SQL to avoid EF value converter decompression
        var nzbFiles = new List<RawNzbFile>();
        var rarFiles = new List<RawRarFile>();
        var multipartFiles = new List<RawMultipartFile>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, SegmentIds, SegmentSizes, SegmentFallbacks FROM DavNzbFiles";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!itemIds.Contains(id)) continue;
                nzbFiles.Add(new RawNzbFile
                {
                    Id = id,
                    SegmentIds = reader.GetString(1),
                    SegmentSizes = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2),
                    SegmentFallbacks = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, RarParts FROM DavRarFiles";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!itemIds.Contains(id)) continue;
                rarFiles.Add(new RawRarFile
                {
                    Id = id,
                    RarParts = reader.GetString(1),
                });
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Metadata FROM DavMultipartFiles";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!itemIds.Contains(id)) continue;
                multipartFiles.Add(new RawMultipartFile
                {
                    Id = id,
                    Metadata = reader.GetString(1),
                });
            }
        }

        var snapshot = new ContentSnapshot
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Items = allItems,
            NzbFiles = nzbFiles,
            RarFiles = rarFiles,
            MultipartFiles = multipartFiles,
        };

        // Serialize and compress
        var tempPath = SnapshotPath + ".tmp";
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

        using (var compressor = new Compressor(1))
        {
            var compressed = compressor.Wrap(jsonBytes);

            if (File.Exists(SnapshotPath))
                File.Copy(SnapshotPath, BackupPath, overwrite: true);

            await File.WriteAllBytesAsync(tempPath, compressed.ToArray(), ct).ConfigureAwait(false);
            File.Move(tempPath, SnapshotPath, overwrite: true);
        }

        // Clean up legacy uncompressed snapshots
        if (File.Exists(LegacySnapshotPath)) File.Delete(LegacySnapshotPath);
        if (File.Exists(LegacyBackupPath)) File.Delete(LegacyBackupPath);

        var fileSizeKb = new FileInfo(SnapshotPath).Length / 1024.0;
        Log.Warning("[ContentSnapshot] Saved snapshot: {Items} items, {Nzb} NZB, {Rar} RAR, {Multipart} multipart ({SizeKb:F0} KB)",
            allItems.Count, nzbFiles.Count, rarFiles.Count, multipartFiles.Count, fileSizeKb);
    }

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
            var fileBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

            using var decompressor = new Decompressor();
            var jsonBytes = decompressor.Unwrap(fileBytes);
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

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    // Snapshot models — file metadata stored as raw DB column values (already compressed)
    private class ContentSnapshot
    {
        public DateTimeOffset CreatedAt { get; set; }
        public List<DavItem> Items { get; set; } = [];
        public List<RawNzbFile> NzbFiles { get; set; } = [];
        public List<RawRarFile> RarFiles { get; set; } = [];
        public List<RawMultipartFile> MultipartFiles { get; set; } = [];
    }

    private class RawNzbFile
    {
        public string Id { get; set; } = "";
        public string SegmentIds { get; set; } = ""; // raw DB TEXT (Zstd+JSON compressed)
        public byte[]? SegmentSizes { get; set; }
        public string? SegmentFallbacks { get; set; } // raw DB TEXT (Zstd+JSON compressed)
    }

    private class RawRarFile
    {
        public string Id { get; set; } = "";
        public string RarParts { get; set; } = ""; // raw DB TEXT (Zstd+JSON compressed)
    }

    private class RawMultipartFile
    {
        public string Id { get; set; } = "";
        public string Metadata { get; set; } = ""; // raw DB TEXT (Zstd+JSON compressed)
    }
}
