namespace DiffusionNexus.UI.Utilities;

/// <summary>Path comparisons that tolerate odd input instead of throwing.</summary>
public static class FilePaths
{
    /// <summary>
    /// Whether two paths name the same file or folder, with trailing separators, casing and separator
    /// spelling forgiven (full paths, compared case-insensitively as on Windows). The comparands often
    /// come from different worlds, such as a <c>Path.Combine</c>-built spelling and a JSON round-trip
    /// of whatever spelling a path was stored under. False when either is null or empty.
    /// </summary>
    public static bool AreSame(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed path cannot be normalized; the literal comparison is the best that is left
            // (and lets a caller holding the stored string itself still find its entry).
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
