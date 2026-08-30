using Avalonia;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services.Lora.Sorting;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One folder (or leaf file) in the LoRA Sorter's "Folder structure preview" tree.</summary>
public partial class SortPreviewNodeViewModel : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Where this row is on disk: for a source row where the file or folder is now, for a
    /// destination row where it would be. Null only on a design-time node.
    /// </summary>
    public string? FullPath { get; init; }

    /// <summary>How deep under the pane's root this row sits. Drives <see cref="Indent"/>.</summary>
    public int Depth { get; init; }

    /// <summary>
    /// Where this row sat among its siblings when the tree was built — i.e. the order the plan
    /// produced. Remembered rather than recomputed so <see cref="PreviewSortOrder.Default"/> can
    /// actually undo a sort instead of merely stopping sorting.
    /// </summary>
    public int Order { get; init; }
    public int LoraCount { get; set; }
    public long TotalBytes { get; set; }
    public bool IsFile { get; init; }
    /// <summary>Dimmed in the view: file already at its computed destination.</summary>
    public bool IsAlreadyInPlace { get; init; }
    /// <summary>Shown with the ✎ marker: file arrives under a collision rename.</summary>
    public bool IsRenamed { get; init; }
    /// <summary>Struck through on the source side: an identical copy is already at the destination,
    /// so this one is not going anywhere. Never appears on the destination side — nothing arrives
    /// for it — which is exactly why the source side has to show it.</summary>
    public bool IsSkippedDuplicate { get; init; }

    /// <summary>
    /// Inside a folder the user told the sorter to leave alone. Excluded rows are drawn dimmed in
    /// both trees but never enter the plan — the file simply stays where it is. Settable rather
    /// than init-only because folders learn it from their files after the tree is assembled; the
    /// build runs synchronously before the first layout pass, the same guarantee
    /// <see cref="Note"/> leans on.
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// The folder whose path is literally on the exclusion list — the one "Sort this folder again"
    /// can act on. A subfolder inside it is excluded too, but un-excluding from there would have
    /// nothing to remove.
    /// </summary>
    public bool IsExclusionRoot { get; set; }

    /// <summary>Whether the context menu offers "Never sort this folder" here: a source-side
    /// folder that is not already excluded. Destination folders describe the plan's output, which
    /// is not a thing one excludes.</summary>
    public bool CanExclude => !IsFile && IsSourceSide && !IsExcluded && !string.IsNullOrEmpty(FullPath);

    /// <summary>Whether the context menu offers "Sort this folder again" here — see
    /// <see cref="IsExclusionRoot"/>.</summary>
    public bool CanUnexclude => !IsFile && IsSourceSide && IsExclusionRoot;

    /// <summary>One dimming flag for the row name: settled files and excluded files both render
    /// dimmed, for the same reason — nothing is going to happen to them.</summary>
    public bool IsDimmed => IsAlreadyInPlace || IsExcluded;
    public ObservableCollection<SortPreviewNodeViewModel> Children { get; } = [];

    /// <summary>This node and everything beneath it, depth first — the walk every subtree-wide
    /// question in the pane needs (the link, the folder notes, the search filter).</summary>
    public IEnumerable<SortPreviewNodeViewModel> SelfAndDescendants()
        => new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));

    /// <summary>Which side of the pane this node was built for. The link runs between the two
    /// trees, so a node has to know which one it is in to find its counterparts in the other.</summary>
    public bool IsSourceSide { get; init; }

    /// <summary>
    /// The planned moves underneath this node — one id per <c>PlannedMove</c>, a file node holding
    /// exactly one and a folder node the union of everything beneath it. This is what pairs the two
    /// trees: the same move is one row on each side, so equal ids mean "these two rows are the same
    /// file, before and after".
    /// </summary>
    public HashSet<int> MoveIds { get; } = [];

    /// <summary>
    /// The short right-aligned line on a source row — "all 7 leave", "already here",
    /// "duplicate — skipped". Deliberately never says a folder <i>empties</i>: the plan covers model
    /// files and their sidecars, not whatever else lives in that folder, and <i>Delete empty source
    /// folders</i> removes a directory only if it is genuinely empty when it runs. Null on the
    /// destination side, where the folder is the answer and there is nothing to add.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>The row the user clicked. At most one across both trees.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>A counterpart of the selected row, on the other tree.</summary>
    [ObservableProperty]
    private bool _isLinked;

    /// <summary>The one counterpart the view scrolls to. A folder click can light dozens of rows;
    /// scrolling to all of them would mean scrolling to none.</summary>
    [ObservableProperty]
    private bool _isPrimaryLink;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Whether the pane's search box is currently letting this row through. True until a
    /// filter says otherwise, so an unfiltered tree draws exactly as it always did.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Formatted through the shared <see cref="FileSizeFormatter"/>, which exists to
    /// consolidate exactly these copies. The private one this replaced used <c>:F1</c> for GB where
    /// the shared formatter and <c>CivitaiDownloadQueue</c> both use <c>:F2</c>, so the sorter's
    /// disk gate read "4.2 GB" while the download queue's gate for the same bytes read "4.20 GB",
    /// on the same screen.</summary>
    public string SizeDisplay => FileSizeFormatter.Format(TotalBytes);

    /// <summary>"12 LoRAs" for a folder, nothing for a file. Kept apart from
    /// <see cref="SizeDisplay"/> so each gets its own fixed-width column and the sizes line up down
    /// the pane whether the row above is a folder or a file.</summary>
    public string CountDisplay => IsFile ? string.Empty : $"{LoraCount} LoRAs";

    /// <summary>
    /// The tree indent, applied to this row's own name rather than to the container holding its
    /// children. Indenting the container moves the row's <i>right</i> edge in by 18px per level too,
    /// which is what left the chips, marks and sizes ragged down the pane.
    /// </summary>
    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    /// <summary>
    /// Whether "Open in folder" has anywhere to go. A destination row usually does not: nothing has
    /// been sorted yet, so the folder the preview is describing does not exist. A file whose folder
    /// exists still counts — opening where it will land is useful even before it lands.
    /// </summary>
    public bool CanOpenInFolder
    {
        get
        {
            if (string.IsNullOrEmpty(FullPath)) return false;
            if (!IsFile) return Directory.Exists(FullPath);
            return File.Exists(FullPath)
                   || (Path.GetDirectoryName(FullPath) is { Length: > 0 } dir && Directory.Exists(dir));
        }
    }

    // Ordered by the enum so the chips read the same way every pass; a folder's chips are the union
    // of everything beneath it, not just its direct children.
    private readonly SortedSet<ModelType> _kinds = [];

    /// <summary>
    /// Chip labels for the asset kinds under this node — "LoRA", "VAE", "ControlNet". A folder that
    /// shows anything besides LoRA is holding something the sorter is not really for, which is the
    /// whole reason these are visible before a move rather than after it.
    /// </summary>
    public ObservableCollection<string> AssetKinds { get; } = [];

    /// <summary>
    /// The worst identity state under this node — <see cref="SortPreviewIdentity.Unidentified"/>
    /// beats <see cref="SortPreviewIdentity.Guessed"/> beats
    /// <see cref="SortPreviewIdentity.Identified"/>. Aggregated through the whole subtree, so a
    /// base-model folder is only "finished" when every category folder beneath it is.
    /// </summary>
    /// <remarks>
    /// Three states, not two, because with <i>Sort by name</i> on there is a real difference the
    /// tree must not paper over: a file whose base model was read from its header or confirmed by
    /// Civitai, and one whose only evidence is that its name contains "pony", both end up with a
    /// non-placeholder base model at this point. Marking the second ✓ under "Every file here has a
    /// base model" made the one screen where a user could audit the lowest-confidence rung assert
    /// the opposite of what it does.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdentified))]
    [NotifyPropertyChangedFor(nameof(IsGuessed))]
    [NotifyPropertyChangedFor(nameof(IsUnidentified))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private SortPreviewIdentity _identity = SortPreviewIdentity.Identified;

    // All three marks stay dark on an excluded row: the marks answer "is this file's destination
    // known", and an excluded file has no destination to know. (Ancestors are protected separately
    // — an excluded file's identity is absorbed as Identified — but the row itself asserting ✓
    // would be a claim about a reading that never happened.)

    /// <summary>✓ — everything here was read or confirmed, not guessed.</summary>
    public bool IsIdentified => !IsExcluded && Identity == SortPreviewIdentity.Identified;

    /// <summary>~ — something here is filed on its file name alone.</summary>
    public bool IsGuessed => !IsExcluded && Identity == SortPreviewIdentity.Guessed;

    /// <summary>✗ — something here has no base model and sorts into Unknown.</summary>
    public bool IsUnidentified => !IsExcluded && Identity == SortPreviewIdentity.Unidentified;

    public string StatusTooltip => Identity switch
    {
        SortPreviewIdentity.Identified => "Every file here has a base model.",
        SortPreviewIdentity.Guessed => IsFile
            ? "This file was named, not read — its folder comes from its file name."
            : "Some files here were named, not read — their folder comes from their file name.",
        _ => IsFile
            ? "This file has no base model — it sorts into Unknown."
            : "Some files here have no base model and sort into Unknown.",
    };

    /// <summary>
    /// Folds one file into this node: its kind joins the chip set, and its identity state is kept
    /// at the worst seen so far. Called for the file's own node and for every ancestor, which is
    /// what makes both values subtree-wide.
    /// </summary>
    public void Absorb(ModelType kind, SortPreviewIdentity identity)
    {
        if (_kinds.Add(kind))
        {
            // Rebuilt rather than appended so the displayed order follows the enum regardless of
            // the order files happen to arrive in — the tree is built incrementally and bound while
            // it is still being built.
            AssetKinds.Clear();
            foreach (var known in _kinds)
                AssetKinds.Add(known.DisplayName());
        }

        if (identity > Identity)
            Identity = identity;
    }
}

/// <summary>How the preview orders the rows inside every folder of both trees.</summary>
public enum PreviewSortOrder
{
    /// <summary>The order the plan produced, untouched. What the pane opens on.</summary>
    Default,

    /// <summary>Biggest first — the order that answers "what is actually taking up the
    /// space".</summary>
    Size,

    /// <summary>A to Z, case-insensitive — the order that answers "where is this one".</summary>
    Name,
}

/// <summary>
/// How well a preview node's base model is known. Ordered worst-last on purpose: the rollup in
/// <see cref="SortPreviewNodeViewModel.Absorb"/> keeps the maximum, so adding a state means placing
/// it correctly in this order rather than editing the rollup.
/// </summary>
public enum SortPreviewIdentity
{
    /// <summary>Read from the file's header, or confirmed by Civitai or a sidecar.</summary>
    Identified,

    /// <summary>Filed on the file name alone, via the opt-in name rung.</summary>
    Guessed,

    /// <summary>No base model at all — sorts into Unknown.</summary>
    Unidentified,
}
