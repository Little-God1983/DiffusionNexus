using System.Globalization;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// The wording and the duration formats the sync surfaces share. The plan dialog, the report
/// dialog and the viewer's status bar are all reachable from the same screen in the same second —
/// the dialog states a verdict and the status bar behind it restates it — so a phrasing that lives
/// in three files is a phrasing that will eventually disagree with itself while every test stays
/// green. One home, referenced from all three.
/// </summary>
internal static class SyncCopy
{
    /// <summary>The verdict for a library with nothing left to do. Shown by the dialog and the status bar.</summary>
    public const string UpToDate = "Library is up to date — nothing to do";

    /// <summary>
    /// The scan's headline. Both dialogs show the same number in the same flow, seconds apart,
    /// so they say it with the same words. An empty string when nothing was found: the surfaces
    /// hide the line rather than printing "0 new files discovered".
    /// </summary>
    public static string DescribeDiscovered(int count) => count switch
    {
        <= 0 => "",
        1 => "1 new file discovered",
        _ => $"{count} new files discovered",
    };

    /// <summary>
    /// A predicted duration: "~45 s" under 90 s, "~4 min" under 90 min, else "~1.5 h". The tilde
    /// is load-bearing — this is what a run is expected to take, not what one took.
    /// </summary>
    /// <remarks>89.5 s reading "~90 s" while still in the seconds branch is the documented quirk, not a bug.</remarks>
    public static string FormatEstimate(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;

        if (t.TotalSeconds < 90)
        {
            return $"~{Math.Round(t.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)} s";
        }

        if (t.TotalMinutes < 90)
        {
            return $"~{Math.Round(t.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)} min";
        }

        return $"~{t.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";
    }

    /// <summary>
    /// A measured duration: "42 s", "3 min 42 s", "1 h 3 min". No tilde — the run is over and this
    /// is what it cost. Two units at most, and a zero minor unit is dropped ("3 min", "1 h"):
    /// beyond the hour mark a stray "0 s" is noise, not precision.
    /// </summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;

        // Rounded to the second first, so 59.7 s reads "1 min" rather than "0 min 60 s".
        var total = (long)Math.Round(t.TotalSeconds);

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;

        if (hours > 0)
        {
            return minutes > 0 ? $"{hours} h {minutes} min" : $"{hours} h";
        }

        if (minutes > 0)
        {
            return seconds > 0 ? $"{minutes} min {seconds} s" : $"{minutes} min";
        }

        return $"{seconds} s";
    }
}
