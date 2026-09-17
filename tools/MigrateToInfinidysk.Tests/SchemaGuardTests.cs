using NzbWebDAV.MigrateToInfinidysk.Io;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class SchemaGuardTests
{
    [Fact]
    public void CheckSource_MissingSharedAncestorMigration_IsInvalid()
    {
        var result = SchemaGuard.CheckSource(["20240101000000_SomeOldMigration"]);

        Assert.False(result.IsValid);
        Assert.Contains("20251113081523", result.ErrorMessage);
    }

    [Fact]
    public void CheckSource_HasSharedAncestorMigration_IsValid()
    {
        var result = SchemaGuard.CheckSource(["20240101000000_Old", SchemaGuard.SharedAncestorMigration, "20260408180402_Newer"]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CheckTarget_MissingRequiredMigration_IsInvalid()
    {
        var result = SchemaGuard.CheckTarget(["20260129182923_Update-DavItems-Type-And-SubType"]);

        Assert.False(result.IsValid);
        Assert.Contains("Add-QueueItem-SortOrder", result.ErrorMessage);
    }

    [Fact]
    public void CheckTarget_HasAllRequiredMigrations_IsValid()
    {
        var result = SchemaGuard.CheckTarget(SchemaGuard.RequiredTargetMigrations);

        Assert.True(result.IsValid);
    }
}
