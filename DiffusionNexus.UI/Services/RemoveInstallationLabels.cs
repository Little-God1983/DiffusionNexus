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
/// Everything about one folder kind stays inside that kind's own label, including the
/// folders being kept and why. A note collected at the bottom of the dialog cannot say
/// which row it belongs to.
/// </summary>
public static class RemoveInstallationLabels
{
    /// <summary>
    /// Labels one cleanup checkbox: the folders this installation can unregister, plus any
    /// it cannot because another installation still uses them, named right there in the
    /// row. "none linked" is reserved for a kind this installation genuinely has none of.
    /// </summary>
    public static string ComposeCheckbox(
        string what,
        IReadOnlyList<string> folders,
        IReadOnlyList<string>? shared = null)
    {
        var kept = Distinct(shared ?? []);

        if (folders.Count == 0 && kept.Count == 0)
        {
            return $"{what} — none linked";
        }

        var lines = new List<string>();

        if (folders.Count > 0)
        {
            lines.Add(what);
            lines.AddRange(folders);

            if (kept.Count > 0)
            {
                lines.Add(KeptHeader(kept.Count, leadingBlankLine: true));
                lines.AddRange(kept);
            }
        }
        else
        {
            // Nothing removable: the heading itself carries the explanation, so the row
            // never reads as though this installation had no folder of this kind.
            lines.Add($"{what} — {KeptHeader(kept.Count, leadingBlankLine: false)}");
            lines.AddRange(kept);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The "kept" explanation, agreeing in number with how many folders it introduces.
    /// </summary>
    private static string KeptHeader(int keptCount, bool leadingBlankLine)
    {
        var subject = keptCount == 1 ? "it" : "them";
        var prefix = leadingBlankLine ? "\n" : string.Empty;
        return $"{prefix}kept, another installation still uses {subject}:";
    }

    /// <summary>De-duplicates paths case-insensitively, preserving order.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
}
