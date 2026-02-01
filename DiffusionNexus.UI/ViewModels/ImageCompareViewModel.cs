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
    /// Gets or sets whether external images are loaded (from Generation Gallery).
    /// </summary>
    [ObservableProperty]
    private bool _isExternalMode;

    /// <summary>
    /// Gets or sets the external images collection when loaded from Generation Gallery.
    /// </summary>
    public List<ImageCompareItem> ExternalImages { get; private set; } = [];

    /// <summary>
    /// The temporary dataset used for external images.
    /// </summary>
    private DatasetCardViewModel? _tempDataset;

    /// <summary>
    /// Loads images from external sources (e.g., Generation Gallery).
    /// </summary>
    /// <param name="imagePaths">The paths to the images to load.</param>
    public void LoadExternalImages(IReadOnlyList<string> imagePaths)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);

        if (imagePaths.Count < 2)
        {
            return;
        }

        // Clear previous external state
        ExternalImages.Clear();

        // Create items from image paths
        foreach (var path in imagePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var displayName = Path.GetFileName(path);
            var item = new ImageCompareItem(path, displayName);
            ExternalImages.Add(item);
        }

        if (ExternalImages.Count < 2)
        {
            return;
        }

        // Create temporary dataset and add to options
        _tempDataset = new DatasetCardViewModel
        {
            Name = "Gallery Selection",
            FolderPath = "TEMP://ImageComparer",
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = ExternalImages.Count,
            TotalImageCountAllVersions = ExternalImages.Count,
            IsTemporary = true
        };

        // Remove any existing temp dataset and add the new one at the beginning
        var existingTemp = DatasetOptions.FirstOrDefault(d => d.IsTemporary);
        if (existingTemp is not null)
        {
            DatasetOptions.Remove(existingTemp);
        }
        DatasetOptions.Insert(0, _tempDataset);

        IsExternalMode = true;

        // Select the temp dataset for both sides (this will trigger the property changed handlers)
        SelectedBeforeDataset = _tempDataset;
        SelectedAfterDataset = _tempDataset;

        IsTrayOpen = true;
        AssignSide = CompareAssignSide.Before;
    }

    /// <summary>
    /// Clears external mode and returns to dataset-based mode.
    /// </summary>
    public void ClearExternalMode()
    {
        if (!IsExternalMode)
        {
            return;
        }

        IsExternalMode = false;
        ExternalImages.Clear();

        // Remove temp dataset from options
        if (_tempDataset is not null)
        {
            DatasetOptions.Remove(_tempDataset);
            _tempDataset = null;
        }

        // Select first available real dataset if current selection was temp
        if (SelectedBeforeDataset?.IsTemporary == true || SelectedBeforeDataset is null)
        {
            SelectedBeforeDataset = DatasetOptions.FirstOrDefault(d => !d.IsTemporary);
        }
        if (SelectedAfterDataset?.IsTemporary == true || SelectedAfterDataset is null)
        {
            SelectedAfterDataset = DatasetOptions.FirstOrDefault(d => !d.IsTemporary);
        }
    }

    /// <summary>
    /// Clears external mode state without changing dataset selections.
    /// Used when both sides have switched away from the temp dataset.
    /// </summary>
    private void ClearExternalModeWithoutDatasetChange()
    {
        IsExternalMode = false;
        ExternalImages.Clear();

        // Remove temp dataset from options
        if (_tempDataset is not null)
        {
            DatasetOptions.Remove(_tempDataset);
            _tempDataset = null;
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
        // If switching from temp to a real dataset on this side, check if we should exit external mode
        if (IsExternalMode && value is not null && !value.IsTemporary)
        {
            // Don't clear external mode here - just let this side use the real dataset
            // Only clear when both sides are no longer using temp
            if (SelectedAfterDataset is null || !SelectedAfterDataset.IsTemporary)
            {
                ClearExternalModeWithoutDatasetChange();
            }
        }

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
        // If switching from temp to a real dataset on this side, check if we should exit external mode
        if (IsExternalMode && value is not null && !value.IsTemporary)
        {
            // Don't clear external mode here - just let this side use the real dataset
            // Only clear when both sides are no longer using temp
            if (SelectedBeforeDataset is null || !SelectedBeforeDataset.IsTemporary)
            {
                ClearExternalModeWithoutDatasetChange();
            }
        }

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
        List<ImageCompareItem> images;

        // Use external images if the selected dataset is the temp dataset
        if (SelectedBeforeDataset?.IsTemporary == true && ExternalImages.Count > 0)
        {
            images = ExternalImages;
        }
        else
        {
            images = LoadImagesFromDataset(SelectedBeforeDataset, SelectedBeforeVersion);
        }

        SelectedBeforeImage = images.FirstOrDefault();

        if (AssignSide == CompareAssignSide.Before)
        {
            RefreshFilmstrip();
        }
    }

    private void RefreshAfterImages()
    {
        List<ImageCompareItem> images;

        // Use external images if the selected dataset is the temp dataset
        if (SelectedAfterDataset?.IsTemporary == true && ExternalImages.Count > 0)
        {
            images = ExternalImages;
            // For after, default to second image if available
            SelectedAfterImage = images.Count > 1 ? images[1] : images.FirstOrDefault();
        }
        else
        {
            images = LoadImagesFromDataset(SelectedAfterDataset, SelectedAfterVersion);
            SelectedAfterImage = images.FirstOrDefault();
        }

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

        List<ImageCompareItem> images;

        // Use external images if the selected dataset is the temp dataset
        if (dataset?.IsTemporary == true && ExternalImages.Count > 0)
        {
            images = ExternalImages;
        }
        else
        {
            images = LoadImagesFromDataset(dataset, version);
        }

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
