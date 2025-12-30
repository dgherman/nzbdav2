# Database Migration & Performance Optimization

## Current Situation

**Database Stats:**
- Size: 256 KB (64 pages × 4KB)
- Fragmentation: None (freelist_count = 0)
- Pending Migrations: 34 migration files
- Migration Speed: **SLOW** - No PRAGMA optimizations

**Problem:**
Running 34 migrations sequentially with default SQLite settings is slow because:
1. Each migration commits with full synchronous writes to disk (PRAGMA synchronous = FULL)
2. Each migration creates/waits for journal file writes
3. No bulk operation optimizations

---

## 🔴 QUICK WINS (Immediate Performance Boost)

### 1. Add PRAGMA Optimizations for Migrations ✅ **RECOMMENDED**

**Impact:** 5-10x faster migrations
**Effort:** 5 minutes
**Risk:** Low (only affects migration performance, not runtime)

Add PRAGMAs before running migrations to speed up bulk operations:

```csharp
// In Program.cs, before databaseContext.Database.MigrateAsync()
if (args.Contains("--db-migration"))
{
    Log.Information("Starting database migration with optimizations...");

    // Apply PRAGMA optimizations for faster migrations
    await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
    await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;");  // Was FULL
    await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA cache_size = -64000;");   // 64MB cache
    await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;");
    await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA mmap_size = 268435456;"); // 256MB mmap

    var argIndex = args.ToList().IndexOf("--db-migration");
    var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
    await databaseContext.Database.MigrateAsync(targetMigration).ConfigureAwait(false);

    Log.Information("Database migration finished.");
    return;
}
```

**Explanation:**
- `journal_mode = WAL`: Write-Ahead Logging for better concurrency
- `synchronous = NORMAL`: Faster writes (still safe, syncs at checkpoints)
- `cache_size = -64000`: 64MB cache for faster operations
- `temp_store = MEMORY`: Store temp tables in RAM
- `mmap_size = 256MB`: Memory-mapped I/O for faster reads

---

### 2. Add Runtime Database Optimizations ✅ **RECOMMENDED**

**Impact:** Better runtime performance (queries, inserts)
**Effort:** 5 minutes
**Risk:** Low

Add optimizations after migrations complete (for normal runtime):

```csharp
// After migrations, apply runtime optimizations
Log.Information("Applying database optimizations...");
await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;");
await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA cache_size = -64000;");
await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;");
await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA mmap_size = 268435456;");
await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 5000;");

// Optional: Run ANALYZE to update query planner statistics
await databaseContext.Database.ExecuteSqlRawAsync("ANALYZE;");
```

---

## 🟡 MEDIUM PRIORITY (Maintenance Tools)

### 3. Database Vacuum Command ⏳

**Impact:** Reclaims unused space, defragments database
**Effort:** 10 minutes
**When to use:** When database grows large or after deleting lots of data

Add a maintenance command:

```csharp
// In Program.cs
if (args.Contains("--db-vacuum"))
{
    Log.Information("Starting database vacuum...");
    await using var databaseContext = new DavDatabaseContext();

    // Get size before
    var sizeBefore = new FileInfo(DavDatabaseContext.DatabaseFilePath).Length;

    await databaseContext.Database.ExecuteSqlRawAsync("VACUUM;");

    // Get size after
    var sizeAfter = new FileInfo(DavDatabaseContext.DatabaseFilePath).Length;
    var saved = sizeBefore - sizeAfter;

    Log.Information("Database vacuum complete. Reclaimed {Bytes} bytes ({Percent:F1}%)",
        saved, (double)saved / sizeBefore * 100);
    return;
}
```

**Usage:**
```bash
dotnet run -- --db-vacuum
```

---

### 4. Database Statistics & Health Check ⏳

**Impact:** Diagnostic information
**Effort:** 15 minutes

Add a database info command:

```csharp
if (args.Contains("--db-info"))
{
    await using var databaseContext = new DavDatabaseContext();

    var pageCount = await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA page_count;");
    var pageSize = await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA page_size;");
    var freelistCount = await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA freelist_count;");
    var journalMode = await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode;");
    var cacheSize = await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA cache_size;");

    var fileSize = new FileInfo(DavDatabaseContext.DatabaseFilePath).Length;
    var wastedBytes = freelistCount * pageSize;

    Log.Information("Database Statistics:");
    Log.Information("  File Size: {Size} bytes ({SizeMB:F2} MB)", fileSize, fileSize / 1024.0 / 1024.0);
    Log.Information("  Pages: {PageCount} × {PageSize} bytes", pageCount, pageSize);
    Log.Information("  Wasted Space: {Wasted} bytes ({Percent:F1}%)", wastedBytes, (double)wastedBytes / fileSize * 100);
    Log.Information("  Journal Mode: {Mode}", journalMode);
    Log.Information("  Cache Size: {Cache} pages", cacheSize);

    return;
}
```

---

## 🟢 ADVANCED OPTIONS (Optional)

### 5. Consolidate Old Migrations 🔧

**Impact:** Fewer migration files to process on fresh installs
**Effort:** High (30-60 minutes, requires careful testing)
**Risk:** Medium (requires thorough testing)
**When:** Only if you frequently create fresh databases

**Process:**
1. Delete existing test database
2. Run all current migrations to create final schema
3. Generate a "squashed" migration that creates the current schema in one step
4. Delete old migration files
5. Test extensively

**Command:**
```bash
# Generate squashed migration
dotnet ef migrations add SquashedMigration_2025

# Manually edit to include all schema changes from all previous migrations
# Delete old migration files (backup first!)
```

**⚠️ WARNING:** This is complex and can break existing databases. Only recommended for new projects or if you control all deployments.

---

### 6. Connection String Optimizations 🔧

**Impact:** Minor performance improvements
**Effort:** Low
**Risk:** Low

Update connection string in `DavDatabaseContext.cs`:

```csharp
.UseSqlite($"Data Source={DatabaseFilePath};Mode=ReadWriteCreate;Cache=Shared;Pooling=True", options =>
{
    options.CommandTimeout(30);
    options.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
})
```

**Parameters:**
- `Cache=Shared`: Share cache between connections
- `Pooling=True`: Enable connection pooling
- `SplitQuery`: Split complex queries for better performance

---

## Implementation Priority

### Phase 1: Immediate (Do Now) ✅
1. **Add PRAGMA optimizations for migrations** (5 min)
2. **Add runtime PRAGMA optimizations** (5 min)

**Expected Result:** 5-10x faster migrations, improved runtime query performance

### Phase 2: Maintenance Tools (Next Week) ⏳
3. **Add --db-vacuum command** (10 min)
4. **Add --db-info command** (15 min)

**Expected Result:** Better maintenance capabilities

### Phase 3: Advanced (Optional) 🔧
5. **Consider migration consolidation** (only if needed)
6. **Connection string optimizations** (minor gains)

---

## Testing & Validation

After implementing PRAGMA optimizations:

```bash
# Time migration with optimizations
time dotnet run -- --db-migration

# Check database health
dotnet run -- --db-info

# If database gets large, vacuum it
dotnet run -- --db-vacuum
```

---

## Current Database Health

**Status:** ✅ HEALTHY
- Size: 256 KB (very small)
- Fragmentation: 0% (no wasted space)
- No vacuum needed currently

**Recommendation:** Implement Phase 1 optimizations immediately for faster migrations. Phase 2 tools are useful but not urgent.

---

## SQLite PRAGMA Reference

### Migration Speed (Temporary Settings)
| PRAGMA | Value | Purpose |
|--------|-------|---------|
| `synchronous` | NORMAL | Faster writes, still safe |
| `journal_mode` | WAL | Write-Ahead Logging |
| `cache_size` | -64000 | 64MB cache |
| `temp_store` | MEMORY | RAM temp tables |
| `mmap_size` | 268435456 | 256MB memory-mapped I/O |

### Runtime Performance (Persistent Settings)
| PRAGMA | Value | Purpose |
|--------|-------|---------|
| `journal_mode` | WAL | Better concurrency |
| `synchronous` | NORMAL | Good balance |
| `cache_size` | -64000 | Faster queries |
| `busy_timeout` | 5000 | Wait on locks |

### Maintenance
| Command | Purpose |
|---------|---------|
| `VACUUM` | Defragment and shrink database |
| `ANALYZE` | Update query planner statistics |
| `PRAGMA optimize` | Auto-analyze |

---

**Last Updated:** 2025-12-30
**Priority:** Implement Phase 1 immediately for 5-10x faster migrations

