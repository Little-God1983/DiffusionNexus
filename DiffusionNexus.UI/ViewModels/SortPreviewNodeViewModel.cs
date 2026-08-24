using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services.Lora.Sorting;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One folder (or leaf file) in the LoRA Sorter's "Folder structure preview" tree.</summary>
public partial class SortPreviewNodeViewModel : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public int LoraCount { get; set; }
    public long TotalBytes { get; set; }
    public bool IsFile { get; init; }
    /// <summary>Dimmed in the view: file already at its computed destination.</summary>
    public bool IsAlreadyInPlace { get; init; }
    /// <summary>Shown with the ✎ marker: file arrives under a collision rename.</summary>
    public bool IsRenamed { get; init; }
    public ObservableCollection<SortPreviewNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Formatted through the shared <see cref="FileSizeFormatter"/>, which exists to
    /// consolidate exactly these copies. The private one this replaced used <c>:F1</c> for GB where
    /// the shared formatter and <c>CivitaiDownloadQueue</c> both use <c>:F2</c>, so the sorter's
    /// disk gate read "4.2 GB" while the download queue's gate for the same bytes read "4.20 GB",
    /// on the same screen.</summary>
    public string CountAndSizeDisplay => IsFile ? FileSizeFormatter.Format(TotalBytes)
        : $"{LoraCount} LoRAs · {FileSizeFormatter.Format(TotalBytes)}";

    // Ordered by the enum so the chips read the same way every pass; a folder's chips are the union
    // of everything beneath it, not just its direct children.
    private readonly SortedSet<SorterAssetKind> _kinds = [];

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

    /// <summary>✓ — everything here was read or confirmed, not guessed.</summary>
    public bool IsIdentified => Identity == SortPreviewIdentity.Identified;

    /// <summary>~ — something here is filed on its file name alone.</summary>
    public bool IsGuessed => Identity == SortPreviewIdentity.Guessed;

    /// <summary>✗ — something here has no base model and sorts into Unknown.</summary>
    public bool IsUnidentified => Identity == SortPreviewIdentity.Unidentified;

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
    public void Absorb(SorterAssetKind kind, SortPreviewIdentity identity)
    {
        if (_kinds.Add(kind))
        {
            // Rebuilt rather than appended so the displayed order follows the enum regardless of
            // the order files happen to arrive in — the tree is built incrementally and bound while
            // it is still being built.
            AssetKinds.Clear();
            foreach (var known in _kinds)
                AssetKinds.Add(SorterAssetKindClassifier.DisplayName(known));
        }

        if (identity > Identity)
            Identity = identity;
    }
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
