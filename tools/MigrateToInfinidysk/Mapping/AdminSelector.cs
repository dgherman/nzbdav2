namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// infinidysk enforces at most one Admin account (IX_Accounts_SingleAdmin unique index).
/// Picks exactly one admin username to import, or fails loudly rather than violate the index.
/// </summary>
public static class AdminSelector
{
    public readonly record struct Result(bool IsSuccess, string? SelectedUsername, string? ErrorMessage)
    {
        public static Result Success(string username) => new(true, username, null);
        public static Result Failure(string message) => new(false, null, message);
    }

    public static Result Select(IReadOnlyList<string> adminUsernames, string? requestedUsername)
    {
        if (adminUsernames.Count == 0)
            return Result.Failure("Source database has no admin account (Accounts.Type = Admin). Nothing to import for the admin account.");

        if (adminUsernames.Count == 1)
            return Result.Success(adminUsernames[0]);

        if (string.IsNullOrEmpty(requestedUsername))
        {
            var choices = string.Join(", ", adminUsernames);
            return Result.Failure(
                $"Source database has {adminUsernames.Count} admin accounts ({choices}), but infinidysk " +
                "allows only one. Re-run with --admin-username <username> to choose which one to import.");
        }

        if (!adminUsernames.Contains(requestedUsername, StringComparer.Ordinal))
        {
            var choices = string.Join(", ", adminUsernames);
            return Result.Failure(
                $"--admin-username '{requestedUsername}' does not match any source admin account. Choices: {choices}");
        }

        return Result.Success(requestedUsername);
    }
}
