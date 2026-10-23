namespace DiffusionNexus.Domain.Utilities;

/// <summary>
/// The one answer to "does this local file live inside that source folder?".
/// </summary>
/// <remarks>
/// <para>
/// It lives in Domain because both sides of the question do: the viewer decides which files it
/// shows (<c>ModelFileSyncService</c>, in Service) and the library sync decides which models it
/// selects (<c>SyncStateRepository</c>, in DataAccess). They used to answer it separately, in two
/// different spellings — the viewer accepted either separator and compared
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, the repository baked in
/// <see cref="Path.DirectorySeparatorChar"/> and folded ASCII only — so a file the viewer listed
/// could be one the sync could not see, and the user got a grid full of models and a plan with
/// nothing in it.
/// </para>
/// <para>
/// The semantics are the viewer's, because that is what the user is looking at: tolerant of a
/// trailing separator on the root, accepting <c>\</c> or <c>/</c> on either side, case-insensitive
/// over the whole of Unicode, and boundary-aware — <c>C:\Loras</c> does not contain
/// <c>C:\Loras_backup\a.safetensors</c>.
/// </para>
/// </remarks>
public static class LocalPathRoots
{
    /// <summary>Whether <paramref name="c"/> separates path segments in either spelling.</summary>
    public static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="root"/> itself or sits inside it.
    /// A blank path, or a root that is blank once its trailing separators are trimmed, is never a
    /// match: "everything" is not what an empty source folder means.
    /// </summary>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(root)) return false;

        var trimmed = root.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return false;

        if (path.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return true;

        // The separator check is what makes it boundary-aware; StartsWith alone would put
        // "C:\Loras_backup\a.safetensors" inside "C:\Loras".
        return path.Length > trimmed.Length
            && path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
            && IsSeparator(path[trimmed.Length]);
    }

    /// <summary>Whether <paramref name="path"/> is under any of <paramref name="roots"/>.</summary>
    public static bool IsUnderAny(string? path, IEnumerable<string?> roots)
    {
        foreach (var root in roots)
        {
            if (IsUnder(path, root)) return true;
        }

        return false;
    }
}
