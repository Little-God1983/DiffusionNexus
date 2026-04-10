using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// Whether there is anything to export.
    /// </summary>
    public bool CanExport => TrainingRuns.Any(r => r.IsSelected && r.TotalSelectedCount > 0);

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
    /// Creates a cancelled result.
    /// </summary>
    public static ExportTrainingRunsResult Cancelled() => new() { Confirmed = false };
}
