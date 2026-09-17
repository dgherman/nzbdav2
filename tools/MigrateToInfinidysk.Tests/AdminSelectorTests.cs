using NzbWebDAV.MigrateToInfinidysk.Mapping;

namespace NzbWebDAV.MigrateToInfinidysk.Tests;

public class AdminSelectorTests
{
    [Fact]
    public void Select_SingleAdmin_ReturnsItWithoutRequestingUsername()
    {
        var result = AdminSelector.Select(new[] { "alice" }, requestedUsername: null);

        Assert.True(result.IsSuccess);
        Assert.Equal("alice", result.SelectedUsername);
    }

    [Fact]
    public void Select_NoAdmins_Fails()
    {
        var result = AdminSelector.Select(Array.Empty<string>(), requestedUsername: null);

        Assert.False(result.IsSuccess);
        Assert.Contains("no admin", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_MultipleAdmins_WithoutFlagChosen_FailsAndListsChoices()
    {
        var result = AdminSelector.Select(new[] { "alice", "bob" }, requestedUsername: null);

        Assert.False(result.IsSuccess);
        Assert.Contains("alice", result.ErrorMessage);
        Assert.Contains("bob", result.ErrorMessage);
        Assert.Contains("--admin-username", result.ErrorMessage);
    }

    [Fact]
    public void Select_MultipleAdmins_WithMatchingFlag_Succeeds()
    {
        var result = AdminSelector.Select(new[] { "alice", "bob" }, requestedUsername: "bob");

        Assert.True(result.IsSuccess);
        Assert.Equal("bob", result.SelectedUsername);
    }

    [Fact]
    public void Select_MultipleAdmins_WithNonMatchingFlag_Fails()
    {
        var result = AdminSelector.Select(new[] { "alice", "bob" }, requestedUsername: "carol");

        Assert.False(result.IsSuccess);
        Assert.Contains("carol", result.ErrorMessage);
    }
}
