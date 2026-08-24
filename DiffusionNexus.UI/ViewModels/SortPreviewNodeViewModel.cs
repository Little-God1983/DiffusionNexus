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
    /// False when anything under this node has no base model — the ✗ state. Aggregates by AND
    /// through the whole subtree, so a base-model folder is only "finished" when every category
    /// folder beneath it is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private bool _isFullyIdentified = true;

    public string StatusTooltip => IsFullyIdentified
        ? "Every file here has a base model."
        : IsFile
            ? "This file has no base model — it sorts into Unknown."
            : "Some files here have no base model and sort into Unknown.";

    /// <summary>
    /// Folds one file into this node: its kind joins the chip set, and its identified state ANDs
    /// into <see cref="IsFullyIdentified"/>. Called for the file's own node and for every ancestor,
    /// which is what makes both values subtree-wide.
    /// </summary>
    public void Absorb(SorterAssetKind kind, bool isIdentified)
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

        if (!isIdentified)
            IsFullyIdentified = false;
    }
}
