using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for selecting and exporting multiple training runs with per-run artifact selection.
/// </summary>
public partial class ExportTrainingRunsDialog : Window
{
    private ExportTrainingRunsDialogViewModel? _viewModel;

    public ExportTrainingRunsDialog()
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
    public ExportTrainingRunsResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with training run data.
    /// </summary>
    /// <param name="datasetName">Name of the dataset.</param>
    /// <param name="datasetVersion">Current version number of the dataset.</param>
    /// <param name="trainingRuns">Training runs available for export.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public ExportTrainingRunsDialog WithData(
        string datasetName,
        int datasetVersion,
        IEnumerable<TrainingRunCardViewModel> trainingRuns)
    {
        _viewModel = new ExportTrainingRunsDialogViewModel(datasetName, datasetVersion, trainingRuns);
        DataContext = _viewModel;
        return this;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = ExportTrainingRunsResult.Cancelled();
            Close(false);
            return;
        }

        var trainingRunResults = new List<TrainingRunExportEntry>();
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

        Result = new ExportTrainingRunsResult
        {
            Confirmed = true,
            TrainingRunResults = trainingRunResults
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = ExportTrainingRunsResult.Cancelled();
        Close(false);
    }
}
