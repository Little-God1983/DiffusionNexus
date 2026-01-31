using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the before/after image comparer experience.
/// Integrates with IDatasetState to load real datasets and their versions.
/// </summary>
public partial class ImageCompareViewModel : ViewModelBase
{
    private readonly IDatasetState? _datasetState;
    private ImageCompareItem? _selectedBeforeImage;
    private ImageCompareItem? _selectedAfterImage;

    /// <summary>
    /// Design-time constructor with empty datasets.
    /// </summary>
    public ImageCompareViewModel()
        : this(null)
    {
    }

    /// <summary>
    /// Runtime constructor with dataset state service.
    /// </summary>
    /// <param name="datasetState">The dataset state service for loading real datasets.</param>
    public ImageCompareViewModel(IDatasetState? datasetState)
    {
        _datasetState = datasetState;

        DatasetOptions = [];
        BeforeVersionOptions = [];
        AfterVersionOptions = [];
        FitModeOptions = [CompareFitMode.Fit, CompareFitMode.Fill, CompareFitMode.OneToOne];
        FilmstripItems = [];

        SwapCommand = new RelayCommand(SwapImages, () => SelectedBeforeImage is not null || SelectedAfterImage is not null);
        ResetSliderCommand = new RelayCommand(ResetSlider);
        AssignImageCommand = new RelayCommand<ImageCompareItem?>(AssignImage);

        AssignSide = CompareAssignSide.Before;
        FitMode = CompareFitMode.Fit;
        SliderValue = 50d;
        IsTrayOpen = false;

        // Subscribe to dataset collection changes to refresh when datasets are loaded
        if (_datasetState is not null)
        {
            _datasetState.Datasets.CollectionChanged += OnDatasetsCollectionChanged;
        }

        LoadDatasets();
    }

    public ObservableCollection<DatasetCardViewModel> DatasetOptions { get; }

    public ObservableCollection<int> BeforeVersionOptions { get; }

    public ObservableCollection<int> AfterVersionOptions { get; }

    public ObservableCollection<ImageCompareItem> FilmstripItems { get; }

    public IReadOnlyList<CompareFitMode> FitModeOptions { get; }

    public IRelayCommand SwapCommand { get; }

    public IRelayCommand ResetSliderCommand { get; }

    public IRelayCommand<ImageCompareItem?> AssignImageCommand { get; }

    [ObservableProperty]
    private DatasetCardViewModel? _selectedBeforeDataset;

    [ObservableProperty]
    private int _selectedBeforeVersion = 1;

    [ObservableProperty]
    private DatasetCardViewModel? _selectedAfterDataset;

    [ObservableProperty]
    private int _selectedAfterVersion = 1;

    [ObservableProperty]
    private CompareAssignSide _assignSide;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private CompareFitMode _fitMode;

    [ObservableProperty]
    private double _sliderValue;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isTrayOpen;

    public ImageCompareItem? SelectedBeforeImage
    {
        get => _selectedBeforeImage;
        set
        {
            if (SetProperty(ref _selectedBeforeImage, value))
            {
                UpdateSelectionFlags(value, isBefore: true);
                OnPropertyChanged(nameof(BeforeImagePath));
                OnPropertyChanged(nameof(BeforeLabel));
                SwapCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ImageCompareItem? SelectedAfterImage
    {
        get => _selectedAfterImage;
        set
        {
            if (SetProperty(ref _selectedAfterImage, value))
            {
                UpdateSelectionFlags(value, isBefore: false);
                OnPropertyChanged(nameof(AfterImagePath));
                OnPropertyChanged(nameof(AfterLabel));
                SwapCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? BeforeImagePath => SelectedBeforeImage?.ImagePath;

    public string? AfterImagePath => SelectedAfterImage?.ImagePath;

    public string BeforeLabel => BuildLabel(SelectedBeforeDataset?.Name, SelectedBeforeVersion, SelectedBeforeImage?.DisplayName, "Before");

    public string AfterLabel => BuildLabel(SelectedAfterDataset?.Name, SelectedAfterVersion, SelectedAfterImage?.DisplayName, "After");

    public double TrayHeight => 260d;

    public double TrayHandleHeight => 34d;

    public double TrayVisibleHeight => IsTrayOpen ? TrayHeight : TrayHandleHeight;

    public bool IsAssigningBefore
    {
        get => AssignSide == CompareAssignSide.Before;
        set
        {
            if (value)
            {
                AssignSide = CompareAssignSide.Before;
            }
        }
    }

    public bool IsAssigningAfter
    {
        get => AssignSide == CompareAssignSide.After;
        set
        {
            if (value)
            {
                AssignSide = CompareAssignSide.After;
            }
        }
    }

    /// <summary>
    /// Handles changes to the datasets collection to refresh when datasets are loaded.
    /// </summary>
    private void OnDatasetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        LoadDatasets();
    }

    /// <summary>
    /// Loads datasets from the dataset state service.
    /// </summary>
    private void LoadDatasets()
    {
        if (_datasetState is null)
        {
            return;
        }

        // Preserve current selections if possible
        var previousBeforeDataset = SelectedBeforeDataset?.Name;
        var previousAfterDataset = SelectedAfterDataset?.Name;

        DatasetOptions.Clear();

        foreach (var dataset in _datasetState.Datasets)
        {
            DatasetOptions.Add(dataset);
        }

        // Restore or set initial selections
        if (DatasetOptions.Count > 0)
        {
            // Try to restore previous selection
            var restoredBefore = previousBeforeDataset is not null 
                ? DatasetOptions.FirstOrDefault(d => d.Name == previousBeforeDataset) 
                : null;
            var restoredAfter = previousAfterDataset is not null 
                ? DatasetOptions.FirstOrDefault(d => d.Name == previousAfterDataset) 
                : null;

            SelectedBeforeDataset = restoredBefore ?? DatasetOptions[0];
            SelectedAfterDataset = restoredAfter ?? (DatasetOptions.Count > 1 ? DatasetOptions[1] : DatasetOptions[0]);
        }
        else
        {
            SelectedBeforeDataset = null;
            SelectedAfterDataset = null;
        }
    }

    /// <summary>
    /// Loads available versions for a dataset.
    /// </summary>
    private void LoadVersionsForDataset(DatasetCardViewModel? dataset, ObservableCollection<int> versionOptions)
    {
        versionOptions.Clear();

        if (dataset is null)
        {
            versionOptions.Add(1);
            return;
        }

        var versions = dataset.GetAllVersionNumbers();
        foreach (var version in versions)
        {
            versionOptions.Add(version);
        }

        if (versionOptions.Count == 0)
        {
            versionOptions.Add(1);
        }
    }

    /// <summary>
    /// Loads images from a dataset version folder.
    /// </summary>
    private List<ImageCompareItem> LoadImagesFromDataset(DatasetCardViewModel? dataset, int version)
    {
        var items = new List<ImageCompareItem>();

        if (dataset is null)
        {
            return items;
        }

        var versionPath = dataset.IsVersionedStructure
            ? dataset.GetVersionFolderPath(version)
            : dataset.FolderPath;

        if (!Directory.Exists(versionPath))
        {
            return items;
        }

        var imageFiles = Directory.EnumerateFiles(versionPath)
            .Where(f => DatasetCardViewModel.IsImageFile(f) && !DatasetCardViewModel.IsVideoThumbnailFile(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var imagePath in imageFiles)
        {
            var displayName = Path.GetFileName(imagePath);
            items.Add(new ImageCompareItem(imagePath, displayName));
        }

        return items;
    }

    partial void OnSelectedBeforeDatasetChanged(DatasetCardViewModel? value)
    {
        LoadVersionsForDataset(value, BeforeVersionOptions);
        SelectedBeforeVersion = BeforeVersionOptions.FirstOrDefault();
        RefreshBeforeImages();
        OnPropertyChanged(nameof(BeforeLabel));
    }

    partial void OnSelectedBeforeVersionChanged(int value)
    {
        RefreshBeforeImages();
        OnPropertyChanged(nameof(BeforeLabel));
    }

    partial void OnSelectedAfterDatasetChanged(DatasetCardViewModel? value)
    {
        LoadVersionsForDataset(value, AfterVersionOptions);
        SelectedAfterVersion = AfterVersionOptions.FirstOrDefault();
        RefreshAfterImages();
        OnPropertyChanged(nameof(AfterLabel));
    }

    partial void OnSelectedAfterVersionChanged(int value)
    {
        RefreshAfterImages();
        OnPropertyChanged(nameof(AfterLabel));
    }

    partial void OnAssignSideChanged(CompareAssignSide value)
    {
        OnPropertyChanged(nameof(IsAssigningBefore));
        OnPropertyChanged(nameof(IsAssigningAfter));
        RefreshFilmstrip();
    }

    partial void OnSearchTextChanged(string? value)
    {
        RefreshFilmstrip();
    }

    partial void OnIsTrayOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(TrayVisibleHeight));
    }

    partial void OnIsPinnedChanged(bool value)
    {
        if (value)
        {
            IsTrayOpen = true;
        }
    }

    private void RefreshBeforeImages()
    {
        var images = LoadImagesFromDataset(SelectedBeforeDataset, SelectedBeforeVersion);
        SelectedBeforeImage = images.FirstOrDefault();

        if (AssignSide == CompareAssignSide.Before)
        {
            RefreshFilmstrip();
        }
    }

    private void RefreshAfterImages()
    {
        var images = LoadImagesFromDataset(SelectedAfterDataset, SelectedAfterVersion);
        SelectedAfterImage = images.FirstOrDefault();

        if (AssignSide == CompareAssignSide.After)
        {
            RefreshFilmstrip();
        }
    }

    public void ResetSlider()
    {
        SliderValue = 50d;
    }

    public void SwapImages()
    {
        var before = SelectedBeforeImage;
        var after = SelectedAfterImage;
        SelectedBeforeImage = after;
        SelectedAfterImage = before;
    }

    public void AssignImage(ImageCompareItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (AssignSide == CompareAssignSide.Before)
        {
            SelectedBeforeImage = item;
        }
        else
        {
            SelectedAfterImage = item;
        }
    }

    private void RefreshFilmstrip()
    {
        FilmstripItems.Clear();

        var dataset = AssignSide == CompareAssignSide.Before ? SelectedBeforeDataset : SelectedAfterDataset;
        var version = AssignSide == CompareAssignSide.Before ? SelectedBeforeVersion : SelectedAfterVersion;

        var images = LoadImagesFromDataset(dataset, version);

        var query = SearchText?.Trim();
        foreach (var item in images)
        {
            if (string.IsNullOrWhiteSpace(query) || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                // Update selection flags based on current selections
                item.IsSelectedBefore = item.ImagePath == SelectedBeforeImage?.ImagePath;
                item.IsSelectedAfter = item.ImagePath == SelectedAfterImage?.ImagePath;
                FilmstripItems.Add(item);
            }
        }
    }

    private void UpdateSelectionFlags(ImageCompareItem? newSelection, bool isBefore)
    {
        foreach (var item in FilmstripItems)
        {
            if (isBefore)
            {
                item.IsSelectedBefore = item == newSelection || item.ImagePath == newSelection?.ImagePath;
            }
            else
            {
                item.IsSelectedAfter = item == newSelection || item.ImagePath == newSelection?.ImagePath;
            }
        }
    }

    private static string BuildLabel(string? datasetName, int version, string? imageName, string fallback)
    {
        var dataset = string.IsNullOrWhiteSpace(datasetName) ? fallback : $"{datasetName} V{version}";
        var image = string.IsNullOrWhiteSpace(imageName) ? "Unassigned" : imageName;
        return $"{dataset} / {image}";
    }
}

public enum CompareAssignSide
{
    Before,
    After
}
