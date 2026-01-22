using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

public enum FileTransferMode
{
    Copy,
    Move
}

public enum DatasetTargetMode
{
    CreateNew,
    Existing
}

public enum DatasetVersionMode
{
    ExistingVersion,
    NewVersion
}

public sealed class AddToDatasetResult
{
    public bool Confirmed { get; init; }
    public FileTransferMode TransferMode { get; init; }
    public DatasetTargetMode TargetMode { get; init; }
    public DatasetVersionMode VersionMode { get; init; }
    public int? TargetVersion { get; init; }
    public DatasetCardViewModel? SelectedDataset { get; init; }
    public CreateDatasetResult? NewDataset { get; init; }

    public static AddToDatasetResult Cancelled() => new() { Confirmed = false };
}

public partial class AddToDatasetDialogViewModel : ObservableObject
{
    private readonly ObservableCollection<int> _availableVersions = [];

    public AddToDatasetDialogViewModel(
        int selectionCount,
        IEnumerable<DatasetCardViewModel> availableDatasets,
        IEnumerable<DatasetCategoryViewModel> availableCategories)
    {
        SelectionCount = selectionCount;
        AvailableDatasets = new ObservableCollection<DatasetCardViewModel>(
            availableDatasets.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase));
        CreateDatasetModel = new CreateDatasetDialogViewModel(availableCategories);
        CreateDatasetModel.PropertyChanged += (_, _) => UpdateCanConfirm();

        IsCopy = true;
        IsCreateNewDataset = AvailableDatasets.Count == 0;
        IsAddToExistingDataset = AvailableDatasets.Count > 0;

        if (AvailableDatasets.Count > 0)
        {
            SelectedDataset = AvailableDatasets[0];
        }

        UseExistingVersion = true;
        CreateNewVersion = false;

        UpdateCanConfirm();
    }

    public int SelectionCount { get; }

    public string SelectionSummary => SelectionCount == 1
        ? "1 file selected"
        : $"{SelectionCount} files selected";

    public ObservableCollection<DatasetCardViewModel> AvailableDatasets { get; }

    public CreateDatasetDialogViewModel CreateDatasetModel { get; }

    public ObservableCollection<int> AvailableVersions => _availableVersions;

    [ObservableProperty]
    private bool _isCopy;

    [ObservableProperty]
    private bool _isMove;

    [ObservableProperty]
    private bool _isCreateNewDataset;

    [ObservableProperty]
    private bool _isAddToExistingDataset;

    [ObservableProperty]
    private DatasetCardViewModel? _selectedDataset;

    [ObservableProperty]
    private int? _selectedVersion;

    [ObservableProperty]
    private bool _useExistingVersion;

    [ObservableProperty]
    private bool _createNewVersion;

    [ObservableProperty]
    private int _nextVersionNumber;

    [ObservableProperty]
    private bool _canConfirm;

    public bool HasExistingDatasets => AvailableDatasets.Count > 0;

    public AddToDatasetResult ToResult()
    {
        var transferMode = IsMove ? FileTransferMode.Move : FileTransferMode.Copy;
        var targetMode = IsCreateNewDataset ? DatasetTargetMode.CreateNew : DatasetTargetMode.Existing;
        var versionMode = CreateNewVersion ? DatasetVersionMode.NewVersion : DatasetVersionMode.ExistingVersion;

        return new AddToDatasetResult
        {
            Confirmed = true,
            TransferMode = transferMode,
            TargetMode = targetMode,
            VersionMode = versionMode,
            TargetVersion = SelectedVersion,
            SelectedDataset = SelectedDataset,
            NewDataset = targetMode == DatasetTargetMode.CreateNew
                ? new CreateDatasetResult
                {
                    Confirmed = true,
                    Name = CreateDatasetModel.GetSanitizedName(),
                    CategoryId = CreateDatasetModel.SelectedCategory?.Id,
                    CategoryOrder = CreateDatasetModel.SelectedCategory?.Order,
                    CategoryName = CreateDatasetModel.SelectedCategory?.Name,
                    Type = CreateDatasetModel.SelectedType,
                    IsNsfw = CreateDatasetModel.IsNsfw
                }
                : null
        };
    }

    partial void OnIsCopyChanged(bool value)
    {
        if (value)
        {
            IsMove = false;
        }
        UpdateCanConfirm();
    }

    partial void OnIsMoveChanged(bool value)
    {
        if (value)
        {
            IsCopy = false;
        }
        UpdateCanConfirm();
    }

    partial void OnIsCreateNewDatasetChanged(bool value)
    {
        if (value)
        {
            IsAddToExistingDataset = false;
        }
        UpdateCanConfirm();
    }

    partial void OnIsAddToExistingDatasetChanged(bool value)
    {
        if (value)
        {
            IsCreateNewDataset = false;
        }
        UpdateCanConfirm();
    }

    partial void OnSelectedDatasetChanged(DatasetCardViewModel? value)
    {
        _availableVersions.Clear();
        if (value is null)
        {
            SelectedVersion = null;
            NextVersionNumber = 0;
            UpdateCanConfirm();
            return;
        }

        foreach (var version in value.GetAllVersionNumbers())
        {
            _availableVersions.Add(version);
        }

        SelectedVersion = value.CurrentVersion > 0
            ? value.CurrentVersion
            : _availableVersions.FirstOrDefault();
        NextVersionNumber = value.GetNextVersionNumber();
        UpdateCanConfirm();
    }

    partial void OnSelectedVersionChanged(int? value)
    {
        UpdateCanConfirm();
    }

    partial void OnUseExistingVersionChanged(bool value)
    {
        if (value)
        {
            CreateNewVersion = false;
        }
        UpdateCanConfirm();
    }

    partial void OnCreateNewVersionChanged(bool value)
    {
        if (value)
        {
            UseExistingVersion = false;
        }
        UpdateCanConfirm();
    }

    private void UpdateCanConfirm()
    {
        var isTransferSelected = IsCopy || IsMove;

        if (!isTransferSelected)
        {
            CanConfirm = false;
            return;
        }

        if (IsCreateNewDataset)
        {
            CanConfirm = CreateDatasetModel.IsValid;
            return;
        }

        if (!IsAddToExistingDataset)
        {
            CanConfirm = false;
            return;
        }

        if (SelectedDataset is null)
        {
            CanConfirm = false;
            return;
        }

        if (CreateNewVersion)
        {
            CanConfirm = true;
            return;
        }

        CanConfirm = UseExistingVersion && SelectedVersion.HasValue;
    }
}
