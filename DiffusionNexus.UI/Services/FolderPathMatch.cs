using System;
using System.IO;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Compares folder paths the way a user means them: <c>E:\AI\comfy_output</c> and
/// <c>E:\AI\comfy_output\</c> are the same folder, and case never matters on Windows.
///
/// Raw <see cref="string.Equals(string, string, StringComparison)"/> on a stored path is a
/// bug waiting to happen — a trailing separator is not part of a folder's identity, but it
/// is part of the string, so the two spellings compare unequal and the same folder gets
/// registered twice.
/// </summary>
public static class FolderPathMatch
{
    /// <summary>
    /// Full path without a trailing separator, or null when the path is blank or invalid.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when both paths name the same folder. Invalid paths never match.</summary>
    public static bool AreSame(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);

        return normalizedLeft is not null
               && normalizedRight is not null
               && normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> or lies beneath it.
    /// Invalid paths never match, and a sibling that merely shares a name prefix
    /// (<c>ComfyUI-Backup</c> vs <c>ComfyUI</c>) is not contained.
    /// </summary>
    public static bool Contains(string? root, string? candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);

        if (normalizedRoot is null || normalizedCandidate is null)
        {
            return false;
        }

        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
