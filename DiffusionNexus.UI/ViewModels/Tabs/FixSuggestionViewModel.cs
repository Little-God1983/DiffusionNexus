using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Wraps a <see cref="FixSuggestion"/> with inline <see cref="EditableAffectedFile"/> editors
/// so that each fix suggestion directly shows its affected files with editing capabilities.
/// </summary>
public class FixSuggestionViewModel
{
    /// <summary>
    /// Human-readable description of what this fix does (e.g. "Replace all with 'car'").
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The underlying <see cref="FixSuggestion"/> used by the Apply Fix command.
    /// </summary>
    public required FixSuggestion Suggestion { get; init; }

    /// <summary>
    /// Per-file edits with inline caption editors attached.
    /// </summary>
    public ObservableCollection<FixEditWithEditor> Edits { get; } = [];

    /// <summary>
    /// Expands all file editors within this suggestion.
    /// </summary>
    public RelayCommand ExpandAllCommand { get; } = null!;

    /// <summary>
    /// Collapses all file editors within this suggestion.
    /// </summary>
    public RelayCommand CollapseAllCommand { get; } = null!;

    public FixSuggestionViewModel()
    {
        ExpandAllCommand = new RelayCommand(ExpandAll);
        CollapseAllCommand = new RelayCommand(CollapseAll);
    }

    private void ExpandAll()
    {
        foreach (var edit in Edits)
        {
            edit.Editor.Expand();
        }
    }

    private void CollapseAll()
    {
        foreach (var edit in Edits)
        {
            edit.Editor.Collapse();
        }
    }
}

/// <summary>
/// Pairs a single <see cref="FileEdit"/> (diff preview) with an <see cref="EditableAffectedFile"/>
/// (expandable caption editor) for the same file.
/// </summary>
public class FixEditWithEditor
{
    private const int ContextChars = 40;

    /// <summary>
    /// The concrete text edit showing original → replacement text.
    /// </summary>
    public required FileEdit Edit { get; init; }

    /// <summary>
    /// The editable file wrapper with expand/collapse, undo/redo, and save support.
    /// Shared across fix suggestions when the same file appears in multiple suggestions.
    /// </summary>
    public required EditableAffectedFile Editor { get; init; }

    /// <summary>
    /// A short snippet of the original text centered on the changed portion,
    /// with "…" ellipsis when the surrounding context is trimmed.
    /// </summary>
    public string DiffOriginalSnippet => BuildSnippet(Edit.OriginalText, isOriginal: true);

    /// <summary>
    /// A short snippet of the new text centered on the changed portion,
    /// with "…" ellipsis when the surrounding context is trimmed.
    /// </summary>
    public string DiffNewSnippet => BuildSnippet(Edit.NewText, isOriginal: false);

    private string BuildSnippet(string text, bool isOriginal)
    {
        var original = Edit.OriginalText ?? string.Empty;
        var updated = Edit.NewText ?? string.Empty;

        // Find the first character that differs
        var prefixLen = 0;
        var minLen = Math.Min(original.Length, updated.Length);

        while (prefixLen < minLen && original[prefixLen] == updated[prefixLen])
            prefixLen++;

        // Find the last character that differs (scanning from the end)
        var suffixLen = 0;

        while (suffixLen < minLen - prefixLen
               && original[^(suffixLen + 1)] == updated[^(suffixLen + 1)])
            suffixLen++;

        // Window: start ContextChars before the change, end ContextChars after
        var windowStart = Math.Max(0, prefixLen - ContextChars);

        var changeEnd = isOriginal
            ? original.Length - suffixLen
            : updated.Length - suffixLen;

        var windowEnd = Math.Min(text.Length, changeEnd + ContextChars);

        var snippet = text[windowStart..windowEnd];
        var prefix = windowStart > 0 ? "…" : "";
        var suffix = windowEnd < text.Length ? "…" : "";

        return $"{prefix}{snippet}{suffix}";
    }
}
