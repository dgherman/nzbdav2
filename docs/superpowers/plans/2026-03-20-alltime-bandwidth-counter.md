# All-Time Bandwidth Counter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the "Total Downloaded ALL TIME" dashboard metric to accumulate bandwidth data before pruning, so the counter reflects true all-time usage rather than just the last 30 days.

**Architecture:** Before the daily maintenance service deletes old `BandwidthSamples`, sum the bytes about to be pruned and add them to a `stats.alltime-bandwidth-bytes` counter in the existing `ConfigItems` table. The dashboard endpoint then returns `archivedBytes + liveBytes` as the all-time total. All operations are wrapped in a transaction for crash safety.

**Tech Stack:** C# / .NET, Entity Framework Core, SQLite, raw ADO.NET for SQL queries

**Spec:** `docs/superpowers/specs/2026-03-20-alltime-bandwidth-counter-design.md`

---

### Task 1: Add accumulate-before-prune logic to DatabaseMaintenanceService

**Files:**
- Modify: `backend/Services/DatabaseMaintenanceService.cs:1-8` (add using directive)
- Modify: `backend/Services/DatabaseMaintenanceService.cs:47-53` (replace bandwidth prune block)

- [ ] **Step 1: Add the missing using directive**

In `backend/Services/DatabaseMaintenanceService.cs`, add after line 5 (`using NzbWebDAV.Database;`):

```csharp
using NzbWebDAV.Database.Models;
```

This is needed because we reference `ConfigItem` in the upsert logic.

- [ ] **Step 2: Replace the bandwidth prune block with accumulate-before-prune**

Replace lines 47-53 (the `// 1. Prune BandwidthSamples (> 30 days)` block):

```csharp
        // 1. Prune BandwidthSamples (> 30 days) — accumulate bytes before deleting
        var bandwidthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

        // Sum bytes about to be pruned (raw SQL to match DELETE pattern)
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
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

        // Delete old samples
        var bandwidthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"BandwidthSamples\" WHERE \"Timestamp\" < {bandwidthCutoff}",
            stoppingToken);
        if (bandwidthDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from BandwidthSamples.", bandwidthDeleted);

        await transaction.CommitAsync(stoppingToken).ConfigureAwait(false);
```

- [ ] **Step 3: Verify the build compiles**

Run: `cd /Users/dgherman/Documents/projects/nzbdav2/backend && dotnet build --no-restore 2>&1 | tail -5`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2
git add backend/Services/DatabaseMaintenanceService.cs
git commit -m "fix: accumulate bandwidth bytes before pruning for all-time counter"
```

---

### Task 2: Update StatsController to use archived counter

**Files:**
- Modify: `backend/Api/Controllers/Stats/StatsController.cs:462-464` (replace all-time query)

- [ ] **Step 1: Replace the all-time bytes query**

In `backend/Api/Controllers/Stats/StatsController.cs`, replace lines 462-464:

```csharp
            var allTimeBytes = await dbContext.BandwidthSamples
                .AsNoTracking()
                .SumAsync(x => x.Bytes);
```

With:

```csharp
            var archivedBytes = long.TryParse(
                (await dbContext.ConfigItems.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.ConfigName == "stats.alltime-bandwidth-bytes"))?.ConfigValue,
                out var parsed) ? parsed : 0L;

            var liveBytes = await dbContext.BandwidthSamples
                .AsNoTracking()
                .SumAsync(x => x.Bytes);

            var allTimeBytes = archivedBytes + liveBytes;
```

- [ ] **Step 2: Verify the build compiles**

Run: `cd /Users/dgherman/Documents/projects/nzbdav2/backend && dotnet build --no-restore 2>&1 | tail -5`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2
git add backend/Api/Controllers/Stats/StatsController.cs
git commit -m "fix: read archived bandwidth counter for all-time total in dashboard"
```

---

### Task 3: Push and verify deployment

**Files:** None (deployment task)

- [ ] **Step 1: Push to GitHub to trigger CI build**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2
git push myfork master
```

- [ ] **Step 2: Verify CI build succeeds**

Check: `gh run list --repo dgherman/nzbdav2 --limit 1`
Expected: workflow run in progress or completed successfully

- [ ] **Step 3: Manual verification after deploy**

After the container is updated:
1. Open the Home page System Dashboard
2. Confirm "All Time" value shows (initially same as current — counter starts at `0`)
3. Wait for the first maintenance cycle (runs 5 minutes after container start, then every 24h)
4. After maintenance runs, check container logs for: `[DatabaseMaintenance] Archived {Bytes} bandwidth bytes to all-time counter`
5. Refresh dashboard — "All Time" should now include the archived bytes
