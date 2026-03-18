using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Unified export dialog combining dataset export and training run export into a single tabbed UI.
/// </summary>
public partial class UnifiedExportDialog : Window
{
    private UnifiedExportDialogViewModel? _viewModel;

    public UnifiedExportDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the export result after the dialog closes.
    /// </summary>
    public UnifiedExportResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with dataset and training run data.
    /// </summary>
    /// <param name="datasetName">Name of the dataset.</param>
    /// <param name="datasetVersion">Current version number of the dataset.</param>
    /// <param name="mediaFiles">Dataset media files.</param>
    /// <param name="trainingRuns">Training runs available for export.</param>
    /// <param name="aiToolkitInstances">Available AI Toolkit installations.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public UnifiedExportDialog WithData(
        string datasetName,
        int datasetVersion,
        IEnumerable<DatasetImageViewModel> mediaFiles,
        IEnumerable<TrainingRunCardViewModel> trainingRuns,
        IEnumerable<InstallerPackage>? aiToolkitInstances = null)
    {
        _viewModel = new UnifiedExportDialogViewModel(datasetName, datasetVersion, mediaFiles, trainingRuns, aiToolkitInstances);
        DataContext = _viewModel;
        return this;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = UnifiedExportResult.Cancelled();
            Close(false);
            return;
        }

        // Build dataset result if Dataset tab is active
        ExportDatasetResult? datasetResult = null;
        if (_viewModel.SelectedTabIndex == 0 && _viewModel.DatasetExport.CanExport)
        {
            var ds = _viewModel.DatasetExport;
            datasetResult = new ExportDatasetResult
            {
                Confirmed = true,
                ExportType = ds.ExportType,
                ExportProductionReady = ds.ExportProductionReady,
                ExportUnrated = ds.ExportUnrated,
                ExportTrash = ds.ExportTrash,
                FilesToExport = ds.GetFilesToExport(),
                AIToolkitInstallationPath = ds.SelectedAIToolkitInstance?.InstallationPath,
                AIToolkitInstanceName = ds.SelectedAIToolkitInstance?.Name,
                AIToolkitFolderName = ds.AIToolkitFolderName,
                AIToolkitConflictMode = ds.AIToolkitConflictMode
            };
        }

        // Build training run entries if Training Runs tab is active
        var trainingRunResults = new List<TrainingRunExportEntry>();
        if (_viewModel.SelectedTabIndex == 1)
        {
            foreach (var run in _viewModel.TrainingRuns.Where(r => r.IsSelected && r.TotalSelectedCount > 0))
            {
                trainingRunResults.Add(new TrainingRunExportEntry
                {
                    Source = run.Source,
                    EpochPaths = run.GetSelectedEpochPaths(),
                    ImagePaths = run.GetSelectedImagePaths(),
                    IncludeModelCard = run.IncludeModelCard,
                    BakeMetadata = run.BakeMetadata
                });
            }
        }

        Result = new UnifiedExportResult
        {
            Confirmed = true,
            DatasetResult = datasetResult,
            TrainingRunResults = trainingRunResults
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = UnifiedExportResult.Cancelled();
        Close(false);
    }
}
