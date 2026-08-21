using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Helpers;

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
}
