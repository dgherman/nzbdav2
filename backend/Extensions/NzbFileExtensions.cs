using System.Text.RegularExpressions;
using Usenet.Nzb;

namespace NzbWebDAV.Extensions;

public static class NzbFileExtensions
{
    public static string[] GetSegmentIds(this NzbFile file)
    {
        return file.Segments
            .Select(x => x.MessageId.Value)
            .ToArray();
    }

    public static string[] GetOrderedSegmentIds(this NzbFile file)
    {
        return file.Segments
            .OrderBy(x => x.Number)
            .Select(x => x.MessageId.Value)
            .ToArray();
    }

    /// <summary>
    /// Returns the primary segment IDs (first occurrence per segment number, ordered)
    /// and a dictionary of fallback candidates for segment numbers that have duplicates.
    /// The dictionary key is the index in the primary array; the value is an array of
    /// alternative message-IDs to try if the primary fails.
    /// </summary>
    public static (string[] PrimaryIds, Dictionary<int, string[]>? Fallbacks) GetSegmentIdsWithFallbacks(this NzbFile file)
    {
        var grouped = file.Segments
            .GroupBy(s => s.Number)
            .OrderBy(g => g.Key)
            .ToList();

        var primaryIds = new string[grouped.Count];
        Dictionary<int, string[]>? fallbacks = null;

        for (int i = 0; i < grouped.Count; i++)
        {
            var candidates = grouped[i].ToList();
            primaryIds[i] = candidates[0].MessageId.Value;

            if (candidates.Count > 1)
            {
                fallbacks ??= new Dictionary<int, string[]>();
                fallbacks[i] = candidates.Skip(1).Select(c => c.MessageId.Value).ToArray();
            }
        }

        return (primaryIds, fallbacks);
    }

    public static long GetTotalYencodedSize(this NzbFile file)
    {
        return file.Size;
    }

    public static string GetSubjectFileName(this NzbFile file)
    {
        return GetFirstValidNonEmptyFilename(
            () => TryParseSubjectFilename1(file),
            () => TryParseSubjectFilename2(file)
        );
    }

    private static string TryParseSubjectFilename1(this NzbFile file)
    {
        // The most common format is when filename appears in double quotes
        // example: `[1/8] - "file.mkv" yEnc 12345 (1/54321)`
        var match = Regex.Match(file.Subject, "\\\"(.*)\\\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string TryParseSubjectFilename2(this NzbFile file)
    {
        // Otherwise, use sabnzbd's regex
        // https://github.com/sabnzbd/sabnzbd/blob/b6b0d10367fd4960bad73edd1d3812cafa7fc002/sabnzbd/nzbstuff.py#L106
        var match = Regex.Match(file.Subject,
            @"\b([\w\-+()' .,]+(?:\[[\w\-\/+()' .,]*][\w\-+()' .,]*)*\.[A-Za-z0-9]{2,4})\b");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string GetFirstValidNonEmptyFilename(params Func<string>[] funcs)
    {
        return funcs
            .Select(x => x.Invoke())
            .Where(x => x == Path.GetFileName(x))
            .FirstOrDefault(x => x != "") ?? "";
    }
}