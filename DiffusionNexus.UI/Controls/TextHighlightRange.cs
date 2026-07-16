namespace DiffusionNexus.UI.Controls;

/// <summary>What a highlighted span means — controls the highlight colour.</summary>
public enum TextHighlightKind
{
    /// <summary>Text that would be removed (red highlight).</summary>
    Removal,

    /// <summary>Text that would be replaced (amber highlight).</summary>
    Change,
}

/// <summary>
/// A character span to highlight inside a <see cref="SpellCheckTextBox"/> (e.g. rule-match
/// previews in the Batch Metadata Distiller). Offsets refer to the current <c>Text</c>;
/// out-of-range spans are skipped at render time.
/// </summary>
public readonly record struct TextHighlightRange(int Start, int Length, TextHighlightKind Kind);
