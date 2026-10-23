using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// The search box over one of the LoRA Sorter's two preview trees. Each pane owns one, filtering
/// only its own tree — the two are deliberately independent, so the source side can be asked
/// "where is this file now?" while the destination side is asked "what is landing in Unknown?".
/// </summary>
/// <remarks>
/// Filters the nodes that already exist rather than re-planning: no disk, no Civitai, no rebuild —
/// which is also what lets a click-to-link highlight survive typing in the box.
/// </remarks>
public sealed partial class SortPreviewFilterViewModel : ObservableObject
{
    private readonly ObservableCollection<SortPreviewNodeViewModel> _roots;

    /// <summary>
    /// Every node's <see cref="SortPreviewNodeViewModel.IsExpanded"/> as it was when the filter
    /// was first typed into, so clearing puts the tree back rather than leaving it splayed open on
    /// whatever the search happened to reveal. Null while no filter is applied.
    /// </summary>
    private Dictionary<SortPreviewNodeViewModel, bool>? _expansionBeforeFilter;

    public SortPreviewFilterViewModel(ObservableCollection<SortPreviewNodeViewModel> roots)
        => _roots = roots;

    [ObservableProperty]
    private string? _text;

    /// <summary>"3 of 1406 files" — null when nothing is being filtered, so an unfiltered pane does
    /// not carry a line that only ever reads "1406 of 1406".</summary>
    [ObservableProperty]
    private string? _summary;

    /// <summary>A filter is applied and it matched nothing. Drives the pane's "No files match"
    /// line: an empty tree under a box with text in it otherwise reads as a broken preview.</summary>
    [ObservableProperty]
    private bool _hasNoMatches;

    [RelayCommand]
    private void Clear() => Text = null;

    partial void OnTextChanged(string? value) => Apply();

    /// <summary>
    /// Re-applies the filter to a tree that has just been rebuilt. The text survives a re-plan —
    /// toggling an option must not silently drop what the user typed — but the nodes it described
    /// do not, which is why the pre-filter expansion snapshot is dropped with them.
    /// </summary>
    public void Reapply()
    {
        _expansionBeforeFilter = null;
        Apply();
    }

    private void Apply()
    {
        var needle = Text?.Trim();

        if (string.IsNullOrEmpty(needle) || _roots.Count == 0)
        {
            Restore();
            return;
        }

        _expansionBeforeFilter ??= AllNodes().ToDictionary(node => node, node => node.IsExpanded);

        // Every keystroke re-filters the tree as the user left it, not as the previous keystroke
        // revealed it: typing "k", "ke", "kee" would otherwise ratchet folders open one at a time
        // and never close them, and clearing the box would leave a tree nobody had expanded.
        RestoreExpansion();

        var visible = 0;
        var total = 0;
        foreach (var root in _roots)
            Filter(root, needle, ancestorMatched: false, ref visible, ref total);

        Summary = $"{visible} of {total} file{(total == 1 ? string.Empty : "s")}";
        HasNoMatches = visible == 0;
    }

    /// <returns>Whether this node survived, so a folder can learn whether anything beneath it did.</returns>
    private static bool Filter(SortPreviewNodeViewModel node, string needle, bool ancestorMatched,
        ref int visible, ref int total)
    {
        var selfMatches = node.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

        if (node.IsFile)
        {
            total++;
            node.IsVisible = ancestorMatched || selfMatches;
            if (node.IsVisible) visible++;
            return node.IsVisible;
        }

        // Matching a folder is a way of asking for the folder, so everything under it stays —
        // otherwise typing a base-model name hands back an empty folder.
        var keepAll = ancestorMatched || selfMatches;

        var anyChildSurvived = false;
        foreach (var child in node.Children)
        {
            // Deliberately not short-circuited: every node still has to be assigned a visibility,
            // and every file still has to be counted, or the tally describes only the subtree up to
            // the first match.
            anyChildSurvived |= Filter(child, needle, keepAll, ref visible, ref total);
        }

        node.IsVisible = keepAll || anyChildSurvived;

        // Opened only for something beneath it: a match inside a collapsed folder is a match nobody
        // sees. A folder that matched on its own name is already the answer to what was typed, so it
        // is left exactly as the user had it.
        if (!keepAll && anyChildSurvived)
            node.IsExpanded = true;

        return node.IsVisible;
    }

    private void Restore()
    {
        foreach (var node in AllNodes())
            node.IsVisible = true;

        RestoreExpansion();
        _expansionBeforeFilter = null;
        Summary = null;
        HasNoMatches = false;
    }

    /// <summary>Puts every node the snapshot still covers back to the expansion it had before the
    /// filter touched it. Nodes it does not cover are new ones, which have never been filtered.</summary>
    private void RestoreExpansion()
    {
        if (_expansionBeforeFilter is null) return;

        foreach (var node in AllNodes())
        {
            if (_expansionBeforeFilter.TryGetValue(node, out var wasExpanded))
                node.IsExpanded = wasExpanded;
        }
    }

    private IEnumerable<SortPreviewNodeViewModel> AllNodes()
        => _roots.SelectMany(root => root.SelfAndDescendants());
}
