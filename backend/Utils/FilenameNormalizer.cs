namespace NzbWebDAV.Utils;

/// <summary>
/// Utility for normalizing filenames to group variants that differ only by extension.
/// This ensures "Movie.2024.mkv" and "Movie.2024" are treated as the same logical file.
/// </summary>
public static class FilenameNormalizer
{
    // Common video/media extensions to strip for normalization
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".avi", ".mp4", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".m4v", ".flv", ".webm", ".vob", ".ogv", ".3gp", ".divx", ".xvid",
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a",
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".nfo", ".txt", ".jpg", ".png",
        ".rar", ".zip", ".7z", ".par2"
    };

    /// <summary>
    /// Normalizes a filename/path for grouping purposes by stripping media extensions.
    /// </summary>
    /// <param name="filename">The filename or path to normalize</param>
    /// <returns>Normalized filename without media extensions</returns>
    public static string Normalize(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return filename ?? "";

        // Get just the filename part if it's a path
        var lastSlash = filename.LastIndexOf('/');
        var directory = lastSlash >= 0 ? filename.Substring(0, lastSlash + 1) : "";
        var name = lastSlash >= 0 ? filename.Substring(lastSlash + 1) : filename;

        // Strip known media extensions
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
        {
            var ext = name.Substring(lastDot);
            if (MediaExtensions.Contains(ext))
            {
                name = name.Substring(0, lastDot);
            }
        }

        // Trim trailing dots and spaces
        name = name.TrimEnd('.', ' ');

        return directory + name;
    }

    /// <summary>
    /// Normalizes just the name portion (no path handling).
    /// Useful for AffinityKey which is typically just a directory/file name.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";

        // Strip known media extensions
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
        {
            var ext = name.Substring(lastDot);
            if (MediaExtensions.Contains(ext))
            {
                name = name.Substring(0, lastDot);
            }
        }

        // Trim trailing dots and spaces
        return name.TrimEnd('.', ' ');
    }
}
