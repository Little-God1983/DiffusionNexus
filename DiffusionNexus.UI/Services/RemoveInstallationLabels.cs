using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Builds the text shown on the remove-installation dialog. Lives outside the window so
/// the wording can be tested without standing up Avalonia — the phrasing carries real
/// meaning here: "none linked" next to a note naming a kept folder is a contradiction the
/// user will (rightly) read as a bug.
/// </summary>
public static class RemoveInstallationLabels
{
    /// <summary>
    /// Labels one cleanup checkbox. Removable folders are listed outright; when the only
    /// folders of that kind are being kept for another installation, the row says so;
    /// "none linked" is reserved for a kind this installation genuinely has none of.
    /// </summary>
    public static string ComposeCheckbox(
        string what,
        IReadOnlyList<string> folders,
        IReadOnlyList<string>? shared = null)
    {
        if (folders.Count > 0)
        {
            return $"{what}\n{string.Join("\n", folders)}";
        }

        return shared is { Count: > 0 }
            ? $"{what} — kept, still used by another installation"
            : $"{what} — none linked";
    }

    /// <summary>
    /// The note naming every folder held back, or an empty string when none were.
    /// Callers show it only when non-empty.
    /// </summary>
    public static string ComposeSharedNote(IEnumerable<string> sharedFolders)
    {
        var folders = Distinct(sharedFolders);
        if (folders.Count == 0)
        {
            return string.Empty;
        }

        var subject = folders.Count == 1 ? "it" : "them";
        return $"Kept because another installation still uses {subject}:\n{string.Join("\n", folders)}";
    }

    /// <summary>De-duplicates paths case-insensitively, preserving order.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
}
