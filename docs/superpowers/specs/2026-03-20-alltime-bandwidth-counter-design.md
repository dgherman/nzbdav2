# All-Time Bandwidth Counter

## Problem

The Home page System Dashboard displays "Total Downloaded ALL TIME" but the value only reflects the last 30 days. The `DatabaseMaintenanceService` prunes `BandwidthSamples` older than 30 days, so the backend query `SumAsync(x => x.Bytes)` over that table can never return more than 30 days of data. The System Monitor 30d view has the same underlying limitation.

## Solution

Maintain a cumulative byte counter in the existing `ConfigItems` table. Before the maintenance service deletes old `BandwidthSamples`, it sums the bytes about to be pruned and adds them to the counter. The dashboard endpoint then returns `archivedBytes + liveSumBytes` as the all-time total.

## Design

### Config key

- **Key:** `stats.alltime-bandwidth-bytes`
- **Value:** String representation of cumulative bytes (e.g., `"549755813888"`)
- **Default:** `0` when key does not exist

No schema migration is required. The `ConfigItems` table already supports arbitrary key-value pairs and keys outside the `ConfigItem.Keys` set are already in use elsewhere.

### DatabaseMaintenanceService changes

In `PerformMaintenanceAsync`, replace the existing `BandwidthSamples` prune block with a transactional accumulate-before-prune sequence. All three steps must execute within a single database transaction to prevent double-counting if the process is interrupted between the counter update and row deletion.

Use raw SQL for the SUM query to match the existing raw SQL DELETE pattern (both operate on unix timestamp integers, avoiding any mismatch with EF Core's `DateTimeOffset` value converter).

Use the existing upsert pattern from `ConfigManager.SaveStaticDownloadKeyAsync`: query for existing row, update or add, then `SaveChangesAsync`.

```csharp
// 1. Accumulate bandwidth bytes before pruning
var bandwidthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();

await using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

// Sum bytes about to be pruned (raw SQL to match DELETE pattern)
var conn = dbContext.Database.GetDbConnection();
if (conn.State != System.Data.ConnectionState.Open)
    await conn.OpenAsync(stoppingToken).ConfigureAwait(false);

await using var cmd = conn.CreateCommand();
var cutoffParam = cmd.CreateParameter();
cutoffParam.ParameterName = "@cutoff";
cutoffParam.Value = bandwidthCutoff;
cmd.Parameters.Add(cutoffParam);
cmd.CommandText = "SELECT COALESCE(SUM(\"Bytes\"), 0) FROM \"BandwidthSamples\" WHERE \"Timestamp\" < @cutoff";
var prunedBytes = Convert.ToInt64(await cmd.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false));

if (prunedBytes > 0)
{
    // Read current counter, upsert with accumulated bytes
    var existing = await dbContext.ConfigItems
        .FirstOrDefaultAsync(c => c.ConfigName == "stats.alltime-bandwidth-bytes", stoppingToken)
        .ConfigureAwait(false);
    var currentTotal = long.TryParse(existing?.ConfigValue, out var parsed) ? parsed : 0L;
    var newTotal = currentTotal + prunedBytes;

    if (existing != null)
        existing.ConfigValue = newTotal.ToString();
    else
        dbContext.ConfigItems.Add(new ConfigItem
            { ConfigName = "stats.alltime-bandwidth-bytes", ConfigValue = newTotal.ToString() });

    await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
    Log.Information("[DatabaseMaintenance] Archived {Bytes} bandwidth bytes to all-time counter (total: {Total}).",
        prunedBytes, newTotal);
}

// Delete old samples (existing logic)
var bandwidthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
    $"DELETE FROM \"BandwidthSamples\" WHERE \"Timestamp\" < {bandwidthCutoff}",
    stoppingToken);
if (bandwidthDeleted > 0)
    Log.Information("[DatabaseMaintenance] Pruned {Count} old records from BandwidthSamples.", bandwidthDeleted);

await transaction.CommitAsync(stoppingToken).ConfigureAwait(false);
```

### StatsController changes

In `GetDashboard`, replace the current all-time query:

```csharp
// Before (bug):
var allTimeBytes = await dbContext.BandwidthSamples
    .AsNoTracking()
    .SumAsync(x => x.Bytes);

// After (fix):
var archivedBytes = long.TryParse(
    (await dbContext.ConfigItems.AsNoTracking()
        .FirstOrDefaultAsync(c => c.ConfigName == "stats.alltime-bandwidth-bytes"))?.ConfigValue,
    out var parsed) ? parsed : 0L;

var liveBytes = await dbContext.BandwidthSamples
    .AsNoTracking()
    .SumAsync(x => x.Bytes);

var allTimeBytes = archivedBytes + liveBytes;
```

### Frontend changes

None. The `TotalDownloaded` component already displays `allTimeBytes` from the API response. The fix is entirely backend.

## Edge cases

- **First run after deploy:** Counter starts at `0`. Live samples (up to 30 days) are still summed. No regression from current behavior. The first maintenance cycle begins accumulating.
- **No rows to prune:** If the sum of bytes-to-delete is `0`, the counter update is skipped. No unnecessary writes.
- **Counter key doesn't exist:** Both the maintenance service and stats controller default to `0`.
- **Concurrency:** The maintenance service is a single background task on a 24h interval. No concurrent writers.
- **Crash safety:** The SUM, upsert, and DELETE are wrapped in a single transaction. If the process crashes mid-operation, the transaction is rolled back — no double-counting, no data loss.
- **Data already pruned before deploy:** Unrecoverable. The counter begins tracking from the first maintenance cycle after deployment. This is expected and acceptable.
- **Overflow:** The counter is a `long` (max ~9.2 exabytes). The `BandwidthSample.Bytes` column is also `long`. No realistic overflow concern.

## Files to modify

1. `backend/Services/DatabaseMaintenanceService.cs` — add accumulate-before-prune logic
2. `backend/Api/Controllers/Stats/StatsController.cs` — read archived counter + live sum for all-time total
