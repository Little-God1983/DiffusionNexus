using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a version entry in the version dropdown, showing training run count.
/// </summary>
public sealed class TrainingRunVersionItem
{
    public int Version { get; init; }
    public int TrainingRunCount { get; init; }

    public string DisplayText => TrainingRunCount == 0
        ? $"V{Version} | No training runs"
        : $"V{Version} | {TrainingRunCount} training run{(TrainingRunCount == 1 ? "" : "s")}";

    public override string ToString() => DisplayText;
}

/// <summary>
/// Result of the Add to Training Run dialog.
/// </summary>
public sealed class AddToTrainingRunResult
{
    public bool Confirmed { get; init; }
    public DatasetImportAction ImportAction { get; init; } = DatasetImportAction.Copy;
    public DatasetCardViewModel? SelectedDataset { get; init; }
    public int? SelectedVersion { get; init; }
    public string? SelectedTrainingRunName { get; init; }
    public bool IsNewTrainingRun { get; init; }
    public string? NewTrainingRunName { get; init; }

    public static AddToTrainingRunResult Cancelled() => new() { Confirmed = false };
}

/// <summary>
/// ViewModel for the Add to Training Run dialog.
/// </summary>
public partial class AddToTrainingRunDialogViewModel : ObservableObject
{
    private readonly int _selectedFileCount;
    private DatasetImportAction _importAction = DatasetImportAction.Copy;
    private DatasetCardViewModel? _selectedDataset;
    private TrainingRunVersionItem? _selectedVersionItem;
    private TrainingRunInfo? _selectedTrainingRun;
    private bool _isCreateNewRun;
    private string _newTrainingRunName = string.Empty;

    public AddToTrainingRunDialogViewModel(int selectedFileCount, IEnumerable<DatasetCardViewModel> availableDatasets)
    {
        _selectedFileCount = selectedFileCount;

        foreach (var dataset in availableDatasets)
        {
            AvailableDatasets.Add(dataset);
        }

        if (AvailableDatasets.Count > 0)
        {
            SelectedDataset = AvailableDatasets[0];
        }
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public AddToTrainingRunDialogViewModel() : this(3, [])
    {
    }

    public ObservableCollection<DatasetCardViewModel> AvailableDatasets { get; } = [];

    public ObservableCollection<TrainingRunVersionItem> VersionItems { get; } = [];

    public ObservableCollection<TrainingRunInfo> ExistingTrainingRuns { get; } = [];

    public string SelectedCountText => _selectedFileCount == 1
        ? "1 image selected"
        : $"{_selectedFileCount} images selected";

    public DatasetImportAction ImportAction
    {
        get => _importAction;
        set
        {
            if (SetProperty(ref _importAction, value))
            {
                OnPropertyChanged(nameof(IsCopy));
                OnPropertyChanged(nameof(IsMove));
            }
        }
    }

    public bool IsCopy
    {
        get => _importAction == DatasetImportAction.Copy;
        set
        {
            if (value) ImportAction = DatasetImportAction.Copy;
        }
    }

    public bool IsMove
    {
        get => _importAction == DatasetImportAction.Move;
        set
        {
            if (value) ImportAction = DatasetImportAction.Move;
        }
    }

    public DatasetCardViewModel? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (SetProperty(ref _selectedDataset, value))
            {
                LoadVersionItems();
            }
        }
    }

    public TrainingRunVersionItem? SelectedVersionItem
    {
        get => _selectedVersionItem;
        set
        {
            if (SetProperty(ref _selectedVersionItem, value))
            {
                LoadTrainingRuns();
                OnPropertyChanged(nameof(HasSelectedVersion));
            }
        }
    }

    public bool HasSelectedVersion => SelectedVersionItem is not null;

    public bool HasExistingTrainingRuns => ExistingTrainingRuns.Count > 0;

    /// <summary>
    /// True when the selected version has no training runs, forcing creation of a new one.
    /// </summary>
    public bool MustCreateNewRun => !HasExistingTrainingRuns && HasSelectedVersion;

    public TrainingRunInfo? SelectedTrainingRun
    {
        get => _selectedTrainingRun;
        set => SetProperty(ref _selectedTrainingRun, value);
    }

    public bool IsCreateNewRun
    {
        get => _isCreateNewRun || MustCreateNewRun;
        set
        {
            if (SetProperty(ref _isCreateNewRun, value))
            {
                OnPropertyChanged(nameof(IsUseExistingRun));
            }
        }
    }

    public bool IsUseExistingRun
    {
        get => !IsCreateNewRun && HasExistingTrainingRuns;
        set
        {
            if (value) IsCreateNewRun = false;
        }
    }

    public string NewTrainingRunName
    {
        get => _newTrainingRunName;
        set
        {
            if (SetProperty(ref _newTrainingRunName, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
            }
        }
    }

    /// <summary>
    /// Whether the confirm button should be enabled.
    /// </summary>
    public bool CanConfirm =>
        SelectedDataset is not null &&
        SelectedVersionItem is not null &&
        (IsCreateNewRun
            ? !string.IsNullOrWhiteSpace(NewTrainingRunName)
            : SelectedTrainingRun is not null);

    public bool HasDatasets => AvailableDatasets.Count > 0;

    private void LoadVersionItems()
    {
        VersionItems.Clear();
        ExistingTrainingRuns.Clear();
        SelectedVersionItem = null;
        SelectedTrainingRun = null;

        if (_selectedDataset is null) return;

        var versions = _selectedDataset.GetAllVersionNumbers();
        foreach (var version in versions)
        {
            var runCount = 0;
            if (_selectedDataset.TrainingRuns.TryGetValue(version, out var runs))
            {
                runCount = runs.Count;
            }

            VersionItems.Add(new TrainingRunVersionItem
            {
                Version = version,
                TrainingRunCount = runCount
            });
        }

        // Select the current version by default
        SelectedVersionItem = VersionItems.FirstOrDefault(
            v => v.Version == _selectedDataset.CurrentVersion) ?? VersionItems.FirstOrDefault();
    }

    private void LoadTrainingRuns()
    {
        ExistingTrainingRuns.Clear();
        SelectedTrainingRun = null;
        _isCreateNewRun = false;

        if (_selectedDataset is null || _selectedVersionItem is null)
        {
            NotifyTrainingRunStateChanged();
            return;
        }

        if (_selectedDataset.TrainingRuns.TryGetValue(_selectedVersionItem.Version, out var runs))
        {
            foreach (var run in runs)
            {
                ExistingTrainingRuns.Add(run);
            }
        }

        if (ExistingTrainingRuns.Count > 0)
        {
            SelectedTrainingRun = ExistingTrainingRuns[0];
        }
        else
        {
            _isCreateNewRun = true;
        }

        NotifyTrainingRunStateChanged();
    }

    private void NotifyTrainingRunStateChanged()
    {
        OnPropertyChanged(nameof(HasExistingTrainingRuns));
        OnPropertyChanged(nameof(MustCreateNewRun));
        OnPropertyChanged(nameof(IsCreateNewRun));
        OnPropertyChanged(nameof(IsUseExistingRun));
        OnPropertyChanged(nameof(CanConfirm));
    }
}
