using Microsoft.Data.Sqlite;
using NzbWebDAV.MigrateToInfinidysk.Io;
using NzbWebDAV.MigrateToInfinidysk.Model;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

/// <summary>
/// Round-18 regression: a real --apply run threw a bare "FOREIGN KEY constraint failed"
/// SqliteException at TargetWriteSession.Commit() (the deferred-FK check from round 14 finally
/// running at commit time), with rollback confirmed clean but no table/rowid to diagnose from -
/// the exception gives no way to tell which row was dangling.
///
/// TargetWriteSession.Commit() now runs `PRAGMA foreign_key_check` BEFORE attempting the real
/// commit, while the pending transaction's rows are still visible to it (confirmed empirically -
/// see SqliteTargetWriter.cs's Commit() doc comment - that a post-commit check would be too late,
/// since SQLite rolls the whole transaction back the instant a deferred FK check fails at
/// COMMIT). This test drives TargetWriteSession directly (not through the whole StreamingMigrator
/// pipeline) to prove the resulting diagnostic names the actual offending table and row, not just
/// the generic SQLite message.
/// </summary>
public class TargetWriteSessionForeignKeyDiagnosticsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fk-diagnostics-{Guid.NewGuid()}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Commit_DanglingForeignKeyInPendingTransaction_ThrowsDiagnosticNamingTableAndRow()
    {
        using var conn = BuildFixtureWithForeignKey();
        var danglingId = Guid.NewGuid();

        using var session = new SqliteTargetWriter.TargetWriteSession(conn);
        // Insert a DavNzbFiles row whose Id has no matching DavItems row anywhere - a genuinely
        // dangling FK reference within the pending transaction, not merely inserted out of order
        // (round 14 already proved deferred checking tolerates out-of-order-but-eventually-
        // present rows; this row's parent is never inserted at all in this test).
        session.InsertDavNzbFile(new TargetDavNzbFile(danglingId, "[\"seg-1\"]"));

        var thrown = Assert.Throws<InvalidOperationException>(() => session.Commit());

        // The whole point of round 18: names the actual table, the actual row (resolved to its
        // real Id, not just an opaque internal rowid), and the table it fails to reference -
        // not just "FOREIGN KEY constraint failed" with nothing to go on.
        Assert.Contains("DavNzbFiles", thrown.Message);
        Assert.Contains(danglingId.ToString(), thrown.Message);
        Assert.Contains("DavItems", thrown.Message);

        // Diagnosing must never itself write partial data or change the existing safe-rollback
        // behavior: Commit() never actually committed (threw before attempting it), so disposing
        // the session without ever calling Commit() successfully still rolls back cleanly.
        session.Dispose();
        using var verifyCmd = conn.CreateCommand();
        verifyCmd.CommandText = "SELECT COUNT(*) FROM DavNzbFiles";
        Assert.Equal(0L, verifyCmd.ExecuteScalar());
    }

    [Fact]
    public void Commit_NoForeignKeyViolations_CommitsNormally()
    {
        // The diagnostic check must not false-positive on ordinary, well-formed writes - this is
        // the same DavItems-then-DavNzbFiles pairing every other passing test in this project
        // relies on working.
        using var conn = BuildFixtureWithForeignKey();
        var id = Guid.NewGuid();

        using (var session = new SqliteTargetWriter.TargetWriteSession(conn))
        {
            session.InsertDavItem(new TargetDavItem(
                id, id.ToString()[..5], DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, "movie.mkv",
                1000, 2, 203, "/content/movie.mkv", null, null, null, HealthRepairPending: false, null));
            session.InsertDavNzbFile(new TargetDavNzbFile(id, "[\"seg-1\"]"));
            session.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DavNzbFiles";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    /// <summary>Real FK constraint (DavNzbFiles.Id -> DavItems.Id), matching infinidysk's actual schema.</summary>
    private SqliteConnection BuildFixtureWithForeignKey()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE DavItems (
                Id TEXT PRIMARY KEY, IdPrefix TEXT NOT NULL, CreatedAt TEXT NOT NULL, ParentId TEXT,
                Name TEXT NOT NULL, FileSize INTEGER, Type INTEGER NOT NULL, SubType INTEGER NOT NULL DEFAULT 0,
                Path TEXT NOT NULL, ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
                HealthRepairPending INTEGER NOT NULL DEFAULT 0, HistoryItemId TEXT);
            CREATE TABLE DavNzbFiles (
                Id TEXT PRIMARY KEY, SegmentIds TEXT NOT NULL,
                FOREIGN KEY (Id) REFERENCES DavItems (Id) ON DELETE CASCADE);
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }
}
