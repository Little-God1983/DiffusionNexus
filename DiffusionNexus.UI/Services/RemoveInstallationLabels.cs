using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Builds the text shown on the remove-installation dialog. Lives outside the window so
/// the wording can be tested without standing up Avalonia — the phrasing carries real
/// meaning here: "none linked" next to a kept folder is a contradiction the user will
/// (rightly) read as a bug.
///
/// Everything about one folder kind stays inside that kind's own row, including the folders
/// being kept and why: an explanation collected at the bottom of the dialog cannot say
/// which row it belongs to. The two halves are returned separately so the view can tint the
/// kept half — it reports something the checkbox will NOT do, and should not read as part
/// of the list of folders it will remove.
/// </summary>
public static class RemoveInstallationLabels
{
    /// <summary>One checkbox's text, split so the view can style the two halves.</summary>
    /// <param name="Text">The kind and the folders it can unregister.</param>
    /// <param name="KeptText">
    /// The folders kept for another installation, with the reason; empty when none were.
    /// </param>
    public sealed record FolderKindLabel(string Text, string KeptText)
    {
        /// <summary>True when this kind has folders that are being kept.</summary>
        public bool HasKept => KeptText.Length > 0;
    }

    /// <summary>
    /// Labels one cleanup checkbox: the folders this installation can unregister, plus any
    /// it cannot because another installation still uses them. "none linked" is reserved
    /// for a kind this installation genuinely has none of.
    /// </summary>
    public static FolderKindLabel Compose(
        string what,
        IReadOnlyList<string> folders,
        IReadOnlyList<string>? shared = null)
    {
        var kept = Distinct(shared ?? []);

        var text = folders.Count > 0
            ? $"{what}\n{string.Join("\n", folders)}"
            : kept.Count > 0
                ? what
                : $"{what} — none linked";

        if (kept.Count == 0)
        {
            return new FolderKindLabel(text, string.Empty);
        }

        var subject = kept.Count == 1 ? "it" : "them";
        return new FolderKindLabel(
            text,
            $"kept — another installation still uses {subject}:\n{string.Join("\n", kept)}");
    }

    /// <summary>De-duplicates paths case-insensitively, preserving order.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
}
