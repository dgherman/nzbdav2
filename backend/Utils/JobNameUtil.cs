namespace NzbWebDAV.Utils;

public static class JobNameUtil
{
    public static string? FromDavPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var directory = Path.GetDirectoryName(path);
        var jobName = Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(jobName) ? null : jobName;
    }

    public static string? PreferJobName(string? currentJobName, string? fileName, string? path)
    {
        var normalizedCurrent = string.IsNullOrWhiteSpace(currentJobName) ? null : currentJobName;
        if (normalizedCurrent != null && !string.Equals(normalizedCurrent, fileName, StringComparison.OrdinalIgnoreCase))
            return normalizedCurrent;

        return FromDavPath(path) ?? normalizedCurrent;
    }
}