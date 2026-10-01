namespace DiffusionNexus.UI.Utilities;

/// <summary>Path comparisons that tolerate odd input instead of throwing.</summary>
public static class FilePaths
{
    /// <summary>
    /// Whether two paths name the same file: full paths compared case-insensitively, as on Windows.
    /// False when either is null or not a valid path.
    /// </summary>
    public static bool AreSame(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="filePath"/> sits directly in <paramref name="folder"/>.</summary>
    public static bool IsDirectlyIn(string? filePath, string? folder)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(folder)) return false;

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return parent is not null && string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
