using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Export Training Run dialog.
/// Lets users select which epochs, images, and whether to include a model card.
/// </summary>
public partial class ExportTrainingRunDialogViewModel : ObservableObject
{
    private readonly string _trainingRunName;
    private bool _includeModelCard = true;

    /// <summary>
    /// Name of the training run being exported.
    /// </summary>
    public string TrainingRunName => _trainingRunName;

    /// <summary>
    /// Selectable epoch files.
    /// </summary>
    public ObservableCollection<SelectableExportItem> Epochs { get; } = [];

    /// <summary>
    /// Selectable media/image files.
    /// </summary>
    public ObservableCollection<SelectableExportItem> Images { get; } = [];

    /// <summary>
    /// Whether to include a model card (README.md) with trigger words, tags, training params, etc.
    /// </summary>
    public bool IncludeModelCard
    {
        get => _includeModelCard;
        set
        {
            if (SetProperty(ref _includeModelCard, value))
            {
                OnPropertyChanged(nameof(TotalSelectedCount));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(ExportModelCardText));
                OnPropertyChanged(nameof(ShowModelCardLine));
                OnPropertyChanged(nameof(ExportTotalText));
            }
        }
    }

    /// <summary>
    /// Number of selected epochs.
    /// </summary>
    public int SelectedEpochCount => Epochs.Count(e => e.IsSelected);

    /// <summary>
    /// Number of selected images.
    /// </summary>
    public int SelectedImageCount => Images.Count(i => i.IsSelected);

    /// <summary>
    /// Total number of items to export (epochs + images + optional model card).
    /// </summary>
    public int TotalSelectedCount => SelectedEpochCount + SelectedImageCount + (IncludeModelCard ? 1 : 0);

    /// <summary>
    /// Whether there is anything to export.
    /// </summary>
    public bool HasSelection => TotalSelectedCount > 0;

    /// <summary>
    /// Display text for safetensor files to export (e.g. "3 Safetensor files").
    /// </summary>
    public string ExportSafetensorText =>
        $"{SelectedEpochCount} Safetensor {(SelectedEpochCount == 1 ? "file" : "files")}";

    /// <summary>
    /// Display text for media files to export (e.g. "5 Media files").
    /// </summary>
    public string ExportMediaText =>
        $"{SelectedImageCount} Media {(SelectedImageCount == 1 ? "file" : "files")}";

    /// <summary>
    /// Display text for model card inclusion (e.g. "1 Model card").
    /// </summary>
    public string ExportModelCardText => IncludeModelCard ? "1 Model card" : "0 Model cards";

    /// <summary>
    /// Whether to show the model card line in the summary.
    /// </summary>
    public bool ShowModelCardLine => IncludeModelCard;

    /// <summary>
    /// Display text for total file count to export (e.g. "9 Files in total").
    /// </summary>
    public string ExportTotalText =>
        $"{TotalSelectedCount} {(TotalSelectedCount == 1 ? "File" : "Files")} in total";

    // ── Commands ──

    public IRelayCommand SelectAllEpochsCommand { get; }
    public IRelayCommand ClearAllEpochsCommand { get; }
    public IRelayCommand SelectAllImagesCommand { get; }
    public IRelayCommand ClearAllImagesCommand { get; }

    /// <summary>
    /// Creates the dialog ViewModel from a training run card.
    /// </summary>
    /// <param name="trainingRun">The training run to export from.</param>
    public ExportTrainingRunDialogViewModel(TrainingRunCardViewModel trainingRun)
    {
        ArgumentNullException.ThrowIfNull(trainingRun);
        _trainingRunName = trainingRun.Name;

        // Populate epochs
        foreach (var epoch in trainingRun.EpochsTab.EpochFiles)
        {
            var item = new SelectableExportItem(epoch.FileName, epoch.FilePath, epoch.FileSizeDisplay)
            {
                IsSelected = false
            };
            item.SelectionChanged += OnItemSelectionChanged;
            Epochs.Add(item);
        }

        // Populate images (all selected by default)
        foreach (var media in trainingRun.PresentationTab.MediaFiles)
        {
            var item = new SelectableExportItem(media.FullFileName, media.ImagePath, null)
            {
                IsSelected = true
            };
            item.SelectionChanged += OnItemSelectionChanged;
            Images.Add(item);
        }

        SelectAllEpochsCommand = new RelayCommand(() => SetAll(Epochs, true));
        ClearAllEpochsCommand = new RelayCommand(() => SetAll(Epochs, false));
        SelectAllImagesCommand = new RelayCommand(() => SetAll(Images, true));
        ClearAllImagesCommand = new RelayCommand(() => SetAll(Images, false));
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ExportTrainingRunDialogViewModel()
    {
        _trainingRunName = "Sample Run";
        SelectAllEpochsCommand = new RelayCommand(() => { });
        ClearAllEpochsCommand = new RelayCommand(() => { });
        SelectAllImagesCommand = new RelayCommand(() => { });
        ClearAllImagesCommand = new RelayCommand(() => { });
    }

    private void OnItemSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedEpochCount));
        OnPropertyChanged(nameof(SelectedImageCount));
        OnPropertyChanged(nameof(TotalSelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ExportSafetensorText));
        OnPropertyChanged(nameof(ExportMediaText));
        OnPropertyChanged(nameof(ExportTotalText));
    }

    private static void SetAll(ObservableCollection<SelectableExportItem> items, bool selected)
    {
        foreach (var item in items)
            item.IsSelected = selected;
    }

    /// <summary>
    /// Gets the file paths of all selected epochs.
    /// </summary>
    public List<string> GetSelectedEpochPaths() =>
        Epochs.Where(e => e.IsSelected).Select(e => e.FilePath).ToList();

    /// <summary>
    /// Gets the file paths of all selected images.
    /// </summary>
    public List<string> GetSelectedImagePaths() =>
        Images.Where(i => i.IsSelected).Select(i => i.FilePath).ToList();
}

/// <summary>
/// A selectable item for the export dialog (epoch file or image file).
/// </summary>
public partial class SelectableExportItem : ObservableObject
{
    private bool _isSelected;

    /// <summary>
    /// Display name (file name).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Full file path.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Optional display size (e.g., "1.2 GB").
    /// </summary>
    public string? SizeDisplay { get; }

    /// <summary>
    /// Whether this item has a size to display.
    /// </summary>
    public bool HasSize => !string.IsNullOrEmpty(SizeDisplay);

    /// <summary>
    /// Whether this item is selected for export.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Raised when the selection state changes.
    /// </summary>
    public event Action? SelectionChanged;

    public SelectableExportItem(string name, string filePath, string? sizeDisplay)
    {
        Name = name;
        FilePath = filePath;
        SizeDisplay = sizeDisplay;
    }
}

/// <summary>
/// Result of the export training run dialog.
/// </summary>
public class ExportTrainingRunResult
{
    /// <summary>
    /// Whether the user confirmed the export.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// Full paths of selected epoch files to export.
    /// </summary>
    public List<string> EpochPaths { get; init; } = [];

    /// <summary>
    /// Full paths of selected image files to export.
    /// </summary>
    public List<string> ImagePaths { get; init; } = [];

    /// <summary>
    /// Whether to include a human-readable model card (README.md).
    /// </summary>
    public bool IncludeModelCard { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static ExportTrainingRunResult Cancelled() => new() { Confirmed = false };
}
