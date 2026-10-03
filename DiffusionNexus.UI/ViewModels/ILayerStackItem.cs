using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// One row of a <c>LayerStackPanel</c>. Implemented by the Image Editor's <see cref="LayerViewModel"/>
/// and by the Diffusion Canvas's <c>GenerationFrameViewModel</c>, so both screens share one layer list.
/// </summary>
/// <remarks>
/// The interface covers only what a row edits in place. Reordering and deleting stay commands on the
/// host's own view model, which is where each screen's rules live (a locked layer cannot be deleted; the
/// editor cannot delete its last layer).
/// </remarks>
public interface ILayerStackItem : INotifyPropertyChanged
{
    /// <summary>Display name. Rename commits through <see cref="LayerStackNaming.Resolve"/>.</summary>
    string Name { get; set; }

    /// <summary>Whether the layer is shown.</summary>
    bool IsVisible { get; set; }

    /// <summary>Whether the layer is protected from removal.</summary>
    bool IsLocked { get; set; }

    /// <summary>Opacity as display text, already formatted invariantly ("75%").</summary>
    string OpacityText { get; }

    /// <summary>Row thumbnail, or null while the layer has no pixels.</summary>
    Bitmap? Thumbnail { get; }
}

/// <summary>The one rule for what a rename commits.</summary>
public static class LayerStackNaming
{
    /// <summary>Longest name kept. A pasted paragraph would otherwise push every row's controls off-screen.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The name to store for <paramref name="proposed"/>: control characters become spaces (a pasted
    /// line break would otherwise land in a single-line row), the result is trimmed and capped, and a
    /// blank result keeps <paramref name="current"/>. A layer always has a name.
    /// </summary>
    /// <summary>
    /// The rename box's own limit: far above <see cref="MaxLength"/>, so normal typing and pasting never
    /// reach it (a box at the cap cut by UTF-16 unit and blocked typing into longer names), yet a wrong
    /// clipboard of megabytes cannot land in a single-line box. <see cref="Resolve"/> makes the cut.
    /// </summary>
    public const int EditorMaxLength = 16 * MaxLength;

    public static string Resolve(string? proposed, string current)
    {
        if (proposed is null)
            return current;

        var cleaned = WithoutLoneSurrogates(new string(proposed.Select(c => char.IsControl(c) ? ' ' : c).ToArray())).Trim();
        if (cleaned.Length == 0)
            return current;

        if (cleaned.Length <= MaxLength)
            return cleaned;

        // Cut by UTF-16 unit, but never between the halves of a surrogate pair (an emoji): a lone half is
        // a broken character on screen and invalid UTF-8 in the TIFF layer names and the logs.
        var length = char.IsHighSurrogate(cleaned[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return cleaned[..length].TrimEnd();
    }

    /// <summary>
    /// Drops any UTF-16 surrogate that is not half of a pair. Text can arrive with one at any length (a
    /// text box that cuts a paste at its own limit cuts by code unit), and a lone half is a broken
    /// character on screen and invalid UTF-8 in the TIFF layer names and the logs.
    /// </summary>
    private static string WithoutLoneSurrogates(string text)
    {
        if (!text.Any(char.IsSurrogate))
            return text;

        var kept = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                kept.Append(text[i]).Append(text[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(text[i]))
            {
                kept.Append(text[i]);
            }
        }

        return kept.ToString();
    }
}
