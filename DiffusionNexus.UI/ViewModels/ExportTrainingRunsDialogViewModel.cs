using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the multi-run training export dialog.
/// Lets users select multiple training runs with per-run epoch/image/model card selections.
/// </summary>
public partial class ExportTrainingRunsDialogViewModel : ObservableObject
{
    /// <summary>
    /// Dialog title displayed at the top.
    /// </summary>
    public string DialogTitle { get; }

    /// <summary>
    /// Selectable training runs, each with its own epochs/images/model card selections.
    /// </summary>
    public ObservableCollection<ExportableTrainingRun> TrainingRuns { get; } = [];

    /// <summary>
    /// Number of training runs selected for export.
    /// </summary>
    public int SelectedRunCount => TrainingRuns.Count(r => r.IsSelected);

    /// <summary>
    /// Summary text of what will be exported.
    /// </summary>
    public string ExportSummary
    {
        get
        {
            var selectedRuns = TrainingRuns.Where(r => r.IsSelected).ToList();
            if (selectedRuns.Count > 0)
            {
                var totalItems = selectedRuns.Sum(r => r.TotalSelectedCount);
                return $"{selectedRuns.Count} run{(selectedRuns.Count == 1 ? "" : "s")} ({totalItems} item{(totalItems == 1 ? "" : "s")})";
            }

            return "Nothing selected";
        }
    }

    /// <summary>
    /// Total number of safetensor files selected across all runs.
    /// </summary>
    private int TotalSafetensorCount => TrainingRuns.Where(r => r.IsSelected).Sum(r => r.SelectedEpochCount);

    /// <summary>
    /// Total number of media files selected across all runs.
    /// </summary>
    private int TotalMediaCount => TrainingRuns.Where(r => r.IsSelected).Sum(r => r.SelectedImageCount);

    /// <summary>
    /// Total number of model cards selected across all runs.
    /// </summary>
    private int TotalModelCardCount => TrainingRuns.Count(r => r.IsSelected && r.IncludeModelCard);

    /// <summary>
    /// Total number of files to export (epochs + images + model cards).
    /// </summary>
    private int TotalFileCount => TotalSafetensorCount + TotalMediaCount + TotalModelCardCount;

    /// <summary>
    /// Display text for runs (e.g. "2 Runs").
    /// </summary>
    public string ExportRunsText =>
        $"{SelectedRunCount} {(SelectedRunCount == 1 ? "Run" : "Runs")}";

    /// <summary>
    /// Display text for model cards (e.g. "2 Model cards").
    /// </summary>
    public string ExportModelCardsText =>
        $"{TotalModelCardCount} Model {(TotalModelCardCount == 1 ? "card" : "cards")}";

    /// <summary>
    /// Display text for safetensor files (e.g. "5 Safetensor files").
    /// </summary>
    public string ExportSafetensorText =>
        $"{TotalSafetensorCount} Safetensor {(TotalSafetensorCount == 1 ? "file" : "files")}";

    /// <summary>
    /// Display text for media files (e.g. "8 Media files").
    /// </summary>
    public string ExportMediaText =>
        $"{TotalMediaCount} Media {(TotalMediaCount == 1 ? "file" : "files")}";

    /// <summary>
    /// Display text for total file count (e.g. "15 Files in total").
    /// </summary>
    public string ExportTotalText =>
        $"{TotalFileCount} {(TotalFileCount == 1 ? "File" : "Files")} in total";

    /// <summary>
    /// Whether there is anything to export.
    /// </summary>
    public bool CanExport => TrainingRuns.Any(r => r.IsSelected && r.TotalSelectedCount > 0);

    /// <summary>
    /// When enabled, exported PNG images will be converted to JPEG (without metadata).
    /// </summary>
    [ObservableProperty]
    private bool _compressImagesToJpeg;

    /// <summary>
    /// Unchecks BakeMetadata on all runs when JPEG compression is enabled,
    /// since JPEG does not support PNG text metadata chunks.
    /// </summary>
    partial void OnCompressImagesToJpegChanged(bool value)
    {
        if (value)
        {
            foreach (var run in TrainingRuns)
            {
                run.BakeMetadata = false;
            }
        }
    }

    // ── Commands ──

    /// <summary>
    /// Selects all training runs and their items.
    /// </summary>
    public IRelayCommand SelectAllRunsCommand { get; }

    /// <summary>
    /// Clears all training run selections.
    /// </summary>
    public IRelayCommand ClearAllRunsCommand { get; }

    /// <summary>
    /// Creates the training runs export ViewModel.
    /// </summary>
    /// <param name="datasetName">Name of the dataset whose training runs are being exported.</param>
    /// <param name="datasetVersion">Current version number of the dataset.</param>
    /// <param name="trainingRuns">Training runs available for export.</param>
    public ExportTrainingRunsDialogViewModel(
        string datasetName,
        int datasetVersion,
        IEnumerable<TrainingRunCardViewModel> trainingRuns)
    {
        DialogTitle = $"Export Training Runs — {datasetName} V{datasetVersion}";

        foreach (var run in trainingRuns)
        {
            var exportableRun = new ExportableTrainingRun(run);
            exportableRun.SelectionChanged += RefreshSummary;
            TrainingRuns.Add(exportableRun);
        }

        SelectAllRunsCommand = new RelayCommand(() =>
        {
            foreach (var run in TrainingRuns)
            {
                run.IsSelected = true;
                run.SelectAllEpochsCommand.Execute(null);
                run.SelectAllImagesCommand.Execute(null);
            }
        });
        ClearAllRunsCommand = new RelayCommand(() =>
        {
            foreach (var run in TrainingRuns)
            {
                run.IsSelected = false;
                run.ClearAllEpochsCommand.Execute(null);
                run.ClearAllImagesCommand.Execute(null);
            }
        });
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ExportTrainingRunsDialogViewModel() : this("Sample Dataset", 1, [])
    {
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(SelectedRunCount));
        OnPropertyChanged(nameof(ExportSummary));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportRunsText));
        OnPropertyChanged(nameof(ExportModelCardsText));
        OnPropertyChanged(nameof(ExportSafetensorText));
        OnPropertyChanged(nameof(ExportMediaText));
        OnPropertyChanged(nameof(ExportTotalText));
    }
}

/// <summary>
/// Result from the training runs export dialog.
/// </summary>
public class ExportTrainingRunsResult
{
    /// <summary>
    /// Whether the user confirmed the export.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// Training run export entries for each selected run.
    /// </summary>
    public List<TrainingRunExportEntry> TrainingRunResults { get; init; } = [];

    /// <summary>
    /// When true, exported PNG images should be converted to JPEG (without metadata).
    /// </summary>
    public bool CompressImagesToJpeg { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static ExportTrainingRunsResult Cancelled() => new() { Confirmed = false };
}

/// <summary>
/// Export entry for a single training run.
/// </summary>
public class TrainingRunExportEntry
{
    /// <summary>
    /// The source training run view model.
    /// </summary>
    public required TrainingRunCardViewModel Source { get; init; }

    /// <summary>
    /// Full paths of selected epoch files.
    /// </summary>
    public List<string> EpochPaths { get; init; } = [];

    /// <summary>
    /// Full paths of selected image files.
    /// </summary>
    public List<string> ImagePaths { get; init; } = [];

    /// <summary>
    /// Whether to include a model card.
    /// </summary>
    public bool IncludeModelCard { get; init; }

    /// <summary>
    /// Whether to embed captions into PNG metadata (A1111-style parameters), overriding existing image metadata.
    /// </summary>
    public bool BakeMetadata { get; init; }
}

/// <summary>
/// Wraps a single training run for multi-select export.
/// Contains selectable epochs, images, and model card toggle.
/// </summary>
public partial class ExportableTrainingRun : ObservableObject
{
    private bool _isSelected = true;
    private bool _isExpanded;
    private bool _includeModelCard = true;
    private bool _bakeMetadata;

    /// <summary>
    /// The underlying training run card view model.
    /// </summary>
    public TrainingRunCardViewModel Source { get; }

    /// <summary>
    /// Display name of the training run.
    /// </summary>
    public string Name => Source.Name;

    /// <summary>
    /// Whether this training run is selected for export.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(TotalSelectedCount));
                SelectionChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Whether the detail panel (epochs/images) is expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Whether to include a model card (README.md).
    /// </summary>
    public bool IncludeModelCard
    {
        get => _includeModelCard;
        set
        {
            if (SetProperty(ref _includeModelCard, value))
            {
                OnPropertyChanged(nameof(TotalSelectedCount));
                SelectionChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Whether to embed captions into PNG metadata (A1111-style parameters), overriding existing image metadata.
    /// </summary>
    public bool BakeMetadata
    {
        get => _bakeMetadata;
        set => SetProperty(ref _bakeMetadata, value);
    }

    /// <summary>
    /// Selectable epoch files for this run.
    /// </summary>
    public ObservableCollection<SelectableExportItem> Epochs { get; } = [];

    /// <summary>
    /// Selectable image files for this run.
    /// </summary>
    public ObservableCollection<SelectableExportItem> Images { get; } = [];

    /// <summary>
    /// Number of selected epochs.
    /// </summary>
    public int SelectedEpochCount => Epochs.Count(e => e.IsSelected);

    /// <summary>
    /// Number of selected images.
    /// </summary>
    public int SelectedImageCount => Images.Count(i => i.IsSelected);

    /// <summary>
    /// Total count of items to export in this run.
    /// </summary>
    public int TotalSelectedCount => SelectedEpochCount + SelectedImageCount + (IncludeModelCard ? 1 : 0);

    /// <summary>
    /// Summary text for this run's selection.
    /// </summary>
    public string ItemSummary
    {
        get
        {
            var parts = new List<string>();
            if (SelectedEpochCount > 0)
                parts.Add($"{SelectedEpochCount} epoch{(SelectedEpochCount == 1 ? "" : "s")}");
            if (SelectedImageCount > 0)
                parts.Add($"{SelectedImageCount} image{(SelectedImageCount == 1 ? "" : "s")}");
            if (IncludeModelCard)
                parts.Add("model card");
            return parts.Count > 0 ? string.Join(", ", parts) : "nothing";
        }
    }

    /// <summary>
    /// Raised when any selection state changes.
    /// </summary>
    public event Action? SelectionChanged;

    // ── Commands ──

    public IRelayCommand SelectAllEpochsCommand { get; }
    public IRelayCommand ClearAllEpochsCommand { get; }
    public IRelayCommand SelectAllImagesCommand { get; }
    public IRelayCommand ClearAllImagesCommand { get; }
    public IRelayCommand ToggleExpandedCommand { get; }

    public ExportableTrainingRun(TrainingRunCardViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;

        // Scan epochs directly from disk (sub-tabs may not be initialized yet)
        var epochsFolder = source.EpochsFolderPath;
        if (Directory.Exists(epochsFolder))
        {
            foreach (var filePath in Directory.EnumerateFiles(epochsFolder)
                         .Where(EpochFileItem.IsEpochFile)
                         .OrderBy(Path.GetFileName))
            {
                var epochItem = EpochFileItem.FromFile(filePath);
                var item = new SelectableExportItem(epochItem.FileName, epochItem.FilePath, epochItem.FileSizeDisplay)
                {
                    IsSelected = true
                };
                item.SelectionChanged += OnItemChanged;
                Epochs.Add(item);
            }
        }

        // Scan presentation media directly from disk
        var presentationFolder = source.PresentationFolderPath;
        if (Directory.Exists(presentationFolder))
        {
            foreach (var filePath in Directory.EnumerateFiles(presentationFolder)
                         .Where(PresentationFileItem.IsMediaFile)
                         .OrderBy(Path.GetFileName))
            {
                var fileName = Path.GetFileName(filePath);
                var item = new SelectableExportItem(fileName, filePath, null)
                {
                    IsSelected = true
                };
                item.SelectionChanged += OnItemChanged;
                Images.Add(item);
            }
        }

        SelectAllEpochsCommand = new RelayCommand(() => SetAll(Epochs, true));
        ClearAllEpochsCommand = new RelayCommand(() => SetAll(Epochs, false));
        SelectAllImagesCommand = new RelayCommand(() => SetAll(Images, true));
        ClearAllImagesCommand = new RelayCommand(() => SetAll(Images, false));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    private void OnItemChanged()
    {
        OnPropertyChanged(nameof(SelectedEpochCount));
        OnPropertyChanged(nameof(SelectedImageCount));
        OnPropertyChanged(nameof(TotalSelectedCount));
        OnPropertyChanged(nameof(ItemSummary));
        SelectionChanged?.Invoke();
    }

    private static void SetAll(ObservableCollection<SelectableExportItem> items, bool selected)
    {
        foreach (var item in items)
            item.IsSelected = selected;
    }

    /// <summary>
    /// Gets file paths of selected epochs.
    /// </summary>
    public List<string> GetSelectedEpochPaths() =>
        Epochs.Where(e => e.IsSelected).Select(e => e.FilePath).ToList();

    /// <summary>
    /// Gets file paths of selected images.
    /// </summary>
    public List<string> GetSelectedImagePaths() =>
        Images.Where(i => i.IsSelected).Select(i => i.FilePath).ToList();
}
