namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

public static class ConfigItemFilter
{
    public readonly record struct FilterResult(
        IReadOnlyList<(string Name, string Value)> Copy,
        IReadOnlyList<(string Name, string Value)> Skip);

    /// <summary>
    /// Splits source ConfigItems rows into ones safe to copy (recognized by infinidysk)
    /// and ones to skip+report (unrecognized name, or nzbdav2's discrete usenet.* keys that
    /// infinidysk replaced with a single "usenet.providers" JSON key of a different shape).
    /// </summary>
    public static FilterResult Filter(IEnumerable<(string Name, string Value)> sourceConfigItems)
    {
        var copy = new List<(string, string)>();
        var skip = new List<(string, string)>();

        foreach (var item in sourceConfigItems)
        {
            if (InfinidyskConfigKeys.IsRecognized(item.Name))
                copy.Add(item);
            else
                skip.Add(item);
        }

        return new FilterResult(copy, skip);
    }
}
