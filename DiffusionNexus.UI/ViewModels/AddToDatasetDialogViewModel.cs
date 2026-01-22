using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

public enum DatasetImportAction
{
    Copy,
    Move
}

public enum DatasetDestinationOption
{
    NewDataset,
    ExistingDataset
}

public enum DatasetVersionOption
{
    UseExistingVersion,
    CreateNewVersion
}

public sealed class AddToDatasetResult
{
    public bool Confirmed { get; init; }
    public DatasetImportAction ImportAction { get; init; } = DatasetImportAction.Copy;
    public DatasetDestinationOption DestinationOption { get; init; } = DatasetDestinationOption.ExistingDataset;
    public DatasetVersionOption VersionOption { get; init; } = DatasetVersionOption.UseExistingVersion;
    public DatasetCardViewModel? SelectedDataset { get; init; }
    public int? SelectedVersion { get; init; }

    public static AddToDatasetResult Cancelled() => new() { Confirmed = false };
}

public partial class AddToDatasetDialogViewModel : ObservableObject
{
    private readonly int _selectedFileCount;
    private DatasetImportAction _importAction = DatasetImportAction.Copy;
    private DatasetDestinationOption _destinationOption = DatasetDestinationOption.ExistingDataset;
    private DatasetVersionOption _versionOption = DatasetVersionOption.UseExistingVersion;
    private DatasetCardViewModel? _selectedDataset;
    private int _selectedVersion;

    public AddToDatasetDialogViewModel(int selectedFileCount, IEnumerable<DatasetCardViewModel> availableDatasets)
    {
        _selectedFileCount = selectedFileCount;

        foreach (var dataset in availableDatasets)
        {
            AvailableDatasets.Add(dataset);
        }

        if (AvailableDatasets.Count == 0)
        {
            _destinationOption = DatasetDestinationOption.NewDataset;
        }
        else
        {
            SelectedDataset = AvailableDatasets[0];
        }
    }

    public AddToDatasetDialogViewModel() : this(3, [])
    {
    }

    public ObservableCollection<DatasetCardViewModel> AvailableDatasets { get; } = [];

    public ObservableCollection<int> AvailableVersions { get; } = [];

    public string SelectedCountText => _selectedFileCount == 1
        ? "1 file selected"
        : $"{_selectedFileCount} files selected";

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
            if (value)
            {
                ImportAction = DatasetImportAction.Copy;
            }
        }
    }

    public bool IsMove
    {
        get => _importAction == DatasetImportAction.Move;
        set
        {
            if (value)
            {
                ImportAction = DatasetImportAction.Move;
            }
        }
    }

    public DatasetDestinationOption DestinationOption
    {
        get => _destinationOption;
        set
        {
            if (SetProperty(ref _destinationOption, value))
            {
                OnPropertyChanged(nameof(IsNewDatasetSelected));
                OnPropertyChanged(nameof(IsExistingDatasetSelected));
                OnPropertyChanged(nameof(IsExistingDatasetEnabled));
            }
        }
    }

    public bool IsNewDatasetSelected
    {
        get => _destinationOption == DatasetDestinationOption.NewDataset;
        set
        {
            if (value)
            {
                DestinationOption = DatasetDestinationOption.NewDataset;
            }
        }
    }

    public bool IsExistingDatasetSelected
    {
        get => _destinationOption == DatasetDestinationOption.ExistingDataset;
        set
        {
            if (value)
            {
                DestinationOption = DatasetDestinationOption.ExistingDataset;
            }
        }
    }

    public bool IsExistingDatasetEnabled => AvailableDatasets.Count > 0;

    public DatasetVersionOption VersionOption
    {
        get => _versionOption;
        set
        {
            if (SetProperty(ref _versionOption, value))
            {
                OnPropertyChanged(nameof(IsUseExistingVersion));
                OnPropertyChanged(nameof(IsCreateNewVersion));
            }
        }
    }

    public bool IsUseExistingVersion
    {
        get => _versionOption == DatasetVersionOption.UseExistingVersion;
        set
        {
            if (value)
            {
                VersionOption = DatasetVersionOption.UseExistingVersion;
            }
        }
    }

    public bool IsCreateNewVersion
    {
        get => _versionOption == DatasetVersionOption.CreateNewVersion;
        set
        {
            if (value)
            {
                VersionOption = DatasetVersionOption.CreateNewVersion;
            }
        }
    }

    public DatasetCardViewModel? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (SetProperty(ref _selectedDataset, value))
            {
                LoadAvailableVersions();
                OnPropertyChanged(nameof(CanCreateNewVersion));
            }
        }
    }

    public int SelectedVersion
    {
        get => _selectedVersion;
        set => SetProperty(ref _selectedVersion, value);
    }

    public bool CanCreateNewVersion => SelectedDataset?.CanIncrementVersion ?? false;

    private void LoadAvailableVersions()
    {
        AvailableVersions.Clear();
        if (_selectedDataset is null) return;

        var versions = _selectedDataset.GetAllVersionNumbers().ToList();
        if (versions.Count == 0)
        {
            versions.Add(_selectedDataset.CurrentVersion);
        }

        foreach (var version in versions)
        {
            AvailableVersions.Add(version);
        }

        SelectedVersion = _selectedDataset.CurrentVersion;
        VersionOption = DatasetVersionOption.UseExistingVersion;
    }
}
