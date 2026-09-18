using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Round-20 regression, PART 2: repair mode (`--repair-guid-casing`) for a target infinidysk
/// db.sqlite this tool already wrote lowercase GUID-shaped TEXT into, in production, before
/// PART 1's tool-side fix existed - see GuidCasingRepair.cs's doc comment for the full incident.
///
/// Seeds a target-shaped DB with mixed-case GUIDs (lowercase Ids on the rows this tool itself
/// wrote, uppercase Id on a row simulating one infinidysk's own EF layer already wrote correctly)
/// spread across several of the authoritative table/column list's tables, including real FK
/// constraints (DavNzbFiles.Id -> DavItems.Id, and a self-referential DavItems.ParentId ->
/// DavItems.Id) with `PRAGMA foreign_keys = ON`, so referential integrity across the case change
/// is actually enforced by SQLite, not just checked with an ordinary (vacuous) join - round 21
/// fixed this fixture after review caught that the original, FK-less version would have passed
/// even against round 20's FK-unsafe repair.
/// </summary>
public class GuidCasingRepairTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"guidcasing-repair-{Guid.NewGuid()}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Run_MixedCaseGuidsAcrossMultipleTables_UppercasesEverythingAndPreservesReferentialIntegrity()
    {
        var davItemId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var alreadyUppercaseQueueItemId = Guid.NewGuid();

        using var conn = BuildFixture();
        SeedMixedCaseRows(conn, davItemId, parentId, alreadyUppercaseQueueItemId);

        var report = GuidCasingRepair.Run(conn);

        Assert.True(report.TotalRowsChanged > 0);

        // Every GUID-shaped column across every table this fixture touched is now uppercase -
        // not just DavItems.Id, the whole authoritative list's worth this fixture exercises.
        AssertAllUppercase(conn, "DavItems", "Id");
        AssertAllUppercase(conn, "DavItems", "ParentId");
        AssertAllUppercase(conn, "DavNzbFiles", "Id");
        AssertAllUppercase(conn, "QueueItems", "Id");

        // The row that was already uppercase (simulating one infinidysk's own EF layer wrote
        // correctly) was never touched at all - no UPDATE matched it, so it's not reported as a
        // change, and its value is exactly what was seeded (not merely "still uppercase").
        Assert.DoesNotContain(report.Changes, c => c.Table == "QueueItems" && c.Column == "Id");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM QueueItems";
            Assert.Equal(alreadyUppercaseQueueItemId.ToString().ToUpperInvariant(), cmd.ExecuteScalar());
        }

        // Referential integrity survives the case change: the DavNzbFiles row (whose Id shares
        // the FK back to DavItems.Id, like the real schema) still joins to its DavItems row by
        // an EF-style typed-parameter lookup - exactly the query shape that failed in
        // production before this repair existed.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT di.Path FROM DavNzbFiles nf
                JOIN DavItems di ON di.Id = nf.Id
                WHERE nf.Id = $id
                """;
            cmd.Parameters.Add(new SqliteParameter("$id", davItemId));
            Assert.Equal("/movie.mkv", cmd.ExecuteScalar());
        }

        // Parent/child join across the case change also still resolves.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM DavItems WHERE ParentId = $parentId";
            cmd.Parameters.Add(new SqliteParameter("$parentId", parentId));
            Assert.Equal(1L, cmd.ExecuteScalar());
        }

        // Row counts are exactly what was seeded - repair only ever rewrites values, never
        // adds/removes rows.
        AssertRowCount(conn, "DavItems", 2);
        AssertRowCount(conn, "DavNzbFiles", 1);
        AssertRowCount(conn, "QueueItems", 1);

        // Idempotent: running again finds nothing left to change.
        var secondReport = GuidCasingRepair.Run(conn);
        Assert.Empty(secondReport.Changes);
        Assert.Equal(0, secondReport.TotalRowsChanged);

        // Still referentially/casing-correct after the idempotent second run.
        AssertAllUppercase(conn, "DavItems", "Id");
        AssertAllUppercase(conn, "DavNzbFiles", "Id");
    }

    [Fact]
    public void Run_TableNotPresentInThisTarget_SkippedWithoutError()
    {
        // A minimal target-shaped DB missing most of the authoritative list's tables (e.g. an
        // older/smaller test fixture) must not make repair mode throw - it should just skip
        // whatever tables aren't there.
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE DavItems (Id TEXT PRIMARY KEY, ParentId TEXT)";
            cmd.ExecuteNonQuery();
        }
        var id = Guid.Parse(Guid.NewGuid().ToString().ToLowerInvariant());
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO DavItems (Id, ParentId) VALUES ($id, NULL)";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }

        var report = GuidCasingRepair.Run(conn);

        Assert.Single(report.Changes);
        Assert.Equal("DavItems", report.Changes[0].Table);
        Assert.Equal("Id", report.Changes[0].Column);
        AssertAllUppercase(conn, "DavItems", "Id");
    }

    /// <summary>
    /// Round 22 (blocking review finding on round 21): GuidCasingRepair.Run() defers FK checks
    /// via PRAGMA defer_foreign_keys=ON (round 21, correct - that only defers WHEN a violation
    /// this transaction's own statements could cause is checked, to COMMIT) but used to call
    /// tx.Commit() directly afterward with no explicit diagnostic. Confirmed empirically
    /// (sabotage-verify): a PRE-EXISTING dangling FK row that this repair's own UPDATEs never
    /// touch (e.g. already uppercase) does not even make a bare tx.Commit() throw - SQLite's
    /// deferred check only re-validates what the current transaction's statements could have
    /// affected, not the whole database - so without the fix, repair mode would silently
    /// "succeed" while real corruption sits undetected, which is worse than an undiagnosed
    /// exception. The fix (PRAGMA foreign_key_check, which DOES scan the whole database) makes
    /// this surface as the same "table X row Y references missing parent Z" message the rest of
    /// this tool already gives (round 18's ForeignKeyDiagnostics, round 22: shared with this
    /// repair path too). This seeds a genuinely dangling DavNzbFiles row - no DavItems row with
    /// that Id exists anywhere, not something the casing repair itself could ever cause or fix -
    /// alongside an ordinary valid lowercase DavItems row that DOES need repairing, so the test
    /// also proves the dangling row's presence doesn't silently swallow or skip the real repair
    /// work; it has to surface as a clear diagnostic instead.
    /// </summary>
    [Fact]
    public void Run_PreExistingDanglingForeignKeyUnrelatedToCasing_SurfacesDescriptiveDiagnosticAndRollsBackCleanly()
    {
        using var conn = BuildFixture();
        var validDavItemId = Guid.NewGuid();
        var danglingNzbFileId = Guid.NewGuid();

        // A normal, valid, lowercase DavItems row - this is what the repair would ordinarily fix.
        InsertRaw(conn, "DavItems",
            "INSERT INTO DavItems (Id, ParentId, Path) VALUES ($id, NULL, $path)",
            ("$id", validDavItemId.ToString().ToLowerInvariant()), ("$path", "/valid.mkv"));

        // A genuinely dangling DavNzbFiles row - no DavItems row with this Id exists at all, and
        // it's already uppercase, so the casing repair itself has no reason to ever touch it.
        // PRAGMA foreign_keys is toggled off only for this one seeding statement (this fixture
        // otherwise enforces real FKs on every other insert in this file) because SQLite enforces
        // FKs per-statement outside a deferred transaction, and this row is deliberately invalid
        // from the moment it's created - there's no valid insert order that avoids that.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys = OFF";
            cmd.ExecuteNonQuery();
        }
        InsertRaw(conn, "DavNzbFiles",
            "INSERT INTO DavNzbFiles (Id, SegmentIds) VALUES ($id, $segIds)",
            ("$id", danglingNzbFileId.ToString().ToUpperInvariant()), ("$segIds", "[\"seg-orphan\"]"));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys = ON";
            cmd.ExecuteNonQuery();
        }

        var thrown = Assert.Throws<InvalidOperationException>(() => GuidCasingRepair.Run(conn));

        // Same descriptive format ForeignKeyDiagnostics already gives TargetWriteSession's
        // callers - not a bare "FOREIGN KEY constraint failed" with nothing to go on.
        Assert.Contains("DavNzbFiles", thrown.Message);
        Assert.Contains(danglingNzbFileId.ToString().ToUpperInvariant(), thrown.Message);
        Assert.Contains("DavItems", thrown.Message);

        // Rolled back cleanly: the valid row's repair never committed either - it's still
        // lowercase, proving this wasn't a partial write (repair work done, then silently lost)
        // but a full, clean rollback of the whole transaction.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM DavItems WHERE Id = $id COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$id", validDavItemId.ToString());
            Assert.Equal(validDavItemId.ToString().ToLowerInvariant(), cmd.ExecuteScalar());
        }
    }

    private static void SeedMixedCaseRows(SqliteConnection conn, Guid davItemId, Guid parentId, Guid alreadyUppercaseQueueItemId)
    {
        // Parent row: lowercase Id, no ParentId of its own.
        InsertRaw(conn, "DavItems",
            "INSERT INTO DavItems (Id, ParentId, Path) VALUES ($id, NULL, $path)",
            ("$id", parentId.ToString().ToLowerInvariant()), ("$path", "/parent"));

        // Child row: lowercase Id AND lowercase ParentId referencing the parent above - both
        // columns need repairing, and the parent/child relationship has to survive it.
        InsertRaw(conn, "DavItems",
            "INSERT INTO DavItems (Id, ParentId, Path) VALUES ($id, $parentId, $path)",
            ("$id", davItemId.ToString().ToLowerInvariant()),
            ("$parentId", parentId.ToString().ToLowerInvariant()),
            ("$path", "/movie.mkv"));

        // DavNzbFiles shares Id as its FK back to DavItems.Id (like the real schema) - also
        // lowercase, also needs repairing, and the join has to still resolve afterward.
        InsertRaw(conn, "DavNzbFiles",
            "INSERT INTO DavNzbFiles (Id, SegmentIds) VALUES ($id, $segIds)",
            ("$id", davItemId.ToString().ToLowerInvariant()), ("$segIds", "[\"seg-1\"]"));

        // QueueItems.Id: already uppercase, simulating a row infinidysk's own EF layer wrote
        // correctly - must be left alone (not reported as a change) and still correct after.
        InsertRaw(conn, "QueueItems",
            "INSERT INTO QueueItems (Id, FileName) VALUES ($id, $fileName)",
            ("$id", alreadyUppercaseQueueItemId.ToString().ToUpperInvariant()), ("$fileName", "file.nzb"));
    }

    private static void InsertRaw(SqliteConnection conn, string table, string sql, params (string Name, string Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static void AssertAllUppercase(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} IS NOT NULL AND {column} <> upper({column})";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    private static void AssertRowCount(SqliteConnection conn, string table, long expected)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        Assert.Equal(expected, cmd.ExecuteScalar());
    }

    /// <summary>
    /// Round 21 (blocking review finding on round 20): the original version of this fixture
    /// declared NO foreign keys at all, so its "referential integrity" assertions were vacuous
    /// ordinary joins - they'd pass even against a repair that violates real FK constraints,
    /// which is exactly what shipped in round 20 and had to be caught by review instead of by
    /// this test. This now declares the same FK shape the real target schema does (and
    /// TargetWriteSessionForeignKeyDiagnosticsTests.cs's fixture already models):
    /// DavNzbFiles.Id -> DavItems.Id, plus a self-referential DavItems.ParentId -> DavItems.Id
    /// (matching the real schema's Id/ParentId relationship), with `PRAGMA foreign_keys = ON`
    /// explicit rather than relying on Microsoft.Data.Sqlite's default.
    /// </summary>
    private SqliteConnection BuildFixture()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, ParentId TEXT, Path TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ParentId) REFERENCES DavItems (Id));
            CREATE TABLE DavNzbFiles (
                Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES DavItems (Id) ON DELETE CASCADE);
            CREATE TABLE QueueItems (Id TEXT PRIMARY KEY, FileName TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }
}
