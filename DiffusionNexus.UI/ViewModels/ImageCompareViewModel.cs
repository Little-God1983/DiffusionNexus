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
/// ViewModel for the left/right image comparer experience.
/// Integrates with IDatasetState to load real datasets and their versions.
/// </summary>
public partial class ImageCompareViewModel : ViewModelBase, IThumbnailAware
{
    private readonly IDatasetState? _datasetState;
    private readonly IThumbnailOrchestrator? _thumbnailOrchestrator;
    private ImageCompareItem? _selectedLeftImage;
    private ImageCompareItem? _selectedRightImage;

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
    public ImageCompareViewModel(IDatasetState? datasetState, IThumbnailOrchestrator? thumbnailOrchestrator = null)
    {
        _datasetState = datasetState;
        _thumbnailOrchestrator = thumbnailOrchestrator;

        DatasetOptions = [];
        LeftVersionOptions = [];
        RightVersionOptions = [];
        FitModeOptions = [CompareFitMode.Fit, CompareFitMode.Fill, CompareFitMode.OneToOne, CompareFitMode.SideBySide];
        FilmstripItems = [];

        SwapCommand = new RelayCommand(SwapImages, () => SelectedLeftImage is not null || SelectedRightImage is not null);
        ResetSliderCommand = new RelayCommand(ResetSlider);
        AssignImageCommand = new RelayCommand<ImageCompareItem?>(AssignImage);

        AssignSide = CompareAssignSide.Left;
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

    public ObservableCollection<int> LeftVersionOptions { get; }

    public ObservableCollection<int> RightVersionOptions { get; }

    public ObservableCollection<ImageCompareItem> FilmstripItems { get; }

    public IReadOnlyList<CompareFitMode> FitModeOptions { get; }

    public IRelayCommand SwapCommand { get; }

    public IRelayCommand ResetSliderCommand { get; }

    public IRelayCommand<ImageCompareItem?> AssignImageCommand { get; }

    [ObservableProperty]
    private DatasetCardViewModel? _selectedLeftDataset;

    [ObservableProperty]
    private int? _selectedLeftVersion = 1;

    [ObservableProperty]
    private DatasetCardViewModel? _selectedRightDataset;

    [ObservableProperty]
    private int? _selectedRightVersion = 1;

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

    /// <summary>
    /// Gets or sets whether single dataset mode is enabled.
    /// When enabled, the Right dataset/version uses the same values as the Left dataset/version.
    /// </summary>
    [ObservableProperty]
    private bool _isSingleDatasetMode = true;

    public ImageCompareItem? SelectedLeftImage
    {
        get => _selectedLeftImage;
        set
        {
            if (SetProperty(ref _selectedLeftImage, value))
            {
                UpdateSelectionFlags(value, isLeft: true);
                OnPropertyChanged(nameof(LeftImagePath));
                OnPropertyChanged(nameof(LeftLabel));
                SwapCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ImageCompareItem? SelectedRightImage
    {
        get => _selectedRightImage;
        set
        {
            if (SetProperty(ref _selectedRightImage, value))
            {
                UpdateSelectionFlags(value, isLeft: false);
                OnPropertyChanged(nameof(RightImagePath));
                OnPropertyChanged(nameof(RightLabel));
                SwapCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? LeftImagePath => SelectedLeftImage?.ImagePath;

    public string? RightImagePath => SelectedRightImage?.ImagePath;

    public string LeftLabel => BuildLabel(SelectedLeftDataset?.Name, SelectedLeftVersion, SelectedLeftImage?.DisplayName, "Left");

    public string RightLabel => BuildLabel(EffectiveRightDataset?.Name, EffectiveRightVersion, SelectedRightImage?.DisplayName, "Right");

    public double TrayHeight => 260d;

    public double TrayHandleHeight => 48d;

    public double TrayVisibleHeight => IsTrayOpen ? TrayHeight : TrayHandleHeight;

    #region IThumbnailAware

    /// <inheritdoc />
    public ThumbnailOwnerToken OwnerToken { get; } = new("ImageComparer");

    /// <inheritdoc />
    public void OnThumbnailActivated()
    {
        _thumbnailOrchestrator?.SetActiveOwner(OwnerToken);
    }

    /// <inheritdoc />
    public void OnThumbnailDeactivated()
    {
        _thumbnailOrchestrator?.CancelRequests(OwnerToken);
    }

    #endregion

    /// <summary>
    /// Gets the effective Right dataset (same as Left when in single dataset mode).
    /// </summary>
    public DatasetCardViewModel? EffectiveRightDataset => IsSingleDatasetMode ? SelectedLeftDataset : SelectedRightDataset;

    /// <summary>
    /// Gets the effective Right version (same as Left when in single dataset mode).
    /// </summary>
    public int? EffectiveRightVersion => IsSingleDatasetMode ? SelectedLeftVersion : SelectedRightVersion;

    public bool IsAssigningLeft
    {
        get => AssignSide == CompareAssignSide.Left;
        set
        {
            if (value)
            {
                AssignSide = CompareAssignSide.Left;
            }
        }
    }

    public bool IsAssigningRight
    {
        get => AssignSide == CompareAssignSide.Right;
        set
        {
            if (value)
            {
                AssignSide = CompareAssignSide.Right;
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
        SelectedLeftDataset = _tempDataset;
        SelectedRightDataset = _tempDataset;

        IsTrayOpen = true;
        AssignSide = CompareAssignSide.Left;
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
        if (SelectedLeftDataset?.IsTemporary == true || SelectedLeftDataset is null)
        {
            SelectedLeftDataset = DatasetOptions.FirstOrDefault(d => !d.IsTemporary);
        }
        if (SelectedRightDataset?.IsTemporary == true || SelectedRightDataset is null)
        {
            SelectedRightDataset = DatasetOptions.FirstOrDefault(d => !d.IsTemporary);
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
        var previousLeftDataset = SelectedLeftDataset?.Name;
        var previousRightDataset = SelectedRightDataset?.Name;

        DatasetOptions.Clear();

        foreach (var dataset in _datasetState.Datasets)
        {
            DatasetOptions.Add(dataset);
        }

        // Restore or set initial selections
        if (DatasetOptions.Count > 0)
        {
            // Try to restore previous selection
            var restoredLeft = previousLeftDataset is not null 
                ? DatasetOptions.FirstOrDefault(d => d.Name == previousLeftDataset) 
                : null;
            var restoredRight = previousRightDataset is not null 
                ? DatasetOptions.FirstOrDefault(d => d.Name == previousRightDataset) 
                : null;

            SelectedLeftDataset = restoredLeft ?? DatasetOptions[0];
            SelectedRightDataset = restoredRight ?? (DatasetOptions.Count > 1 ? DatasetOptions[1] : DatasetOptions[0]);
        }
        else
        {
            SelectedLeftDataset = null;
            SelectedRightDataset = null;
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

        // Temporary datasets always have version 1 only
        if (dataset.IsTemporary)
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
    private List<ImageCompareItem> LoadImagesFromDataset(DatasetCardViewModel? dataset, int? version)
    {
        var items = new List<ImageCompareItem>();

        if (dataset is null)
        {
            return items;
        }

        var versionNum = version ?? 1;
        var versionPath = dataset.IsVersionedStructure
            ? dataset.GetVersionFolderPath(versionNum)
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

    partial void OnSelectedLeftDatasetChanged(DatasetCardViewModel? value)
    {
        // If switching from temp to a real dataset on this side, check if we should exit external mode
        if (IsExternalMode && value is not null && !value.IsTemporary)
        {
            // Don't clear external mode here - just let this side use the real dataset
            // Only clear when both sides are no longer using temp
            if (SelectedRightDataset is null || !SelectedRightDataset.IsTemporary)
            {
                ClearExternalModeWithoutDatasetChange();
            }
        }

        LoadVersionsForDataset(value, LeftVersionOptions);
        SelectedLeftVersion = LeftVersionOptions.FirstOrDefault();
        RefreshLeftImages();
        OnPropertyChanged(nameof(LeftLabel));

        // In single dataset mode, the Right side uses the same dataset
        if (IsSingleDatasetMode)
        {
            OnPropertyChanged(nameof(EffectiveRightDataset));
            OnPropertyChanged(nameof(EffectiveRightVersion));
            RefreshRightImages();
            OnPropertyChanged(nameof(RightLabel));
        }
    }

    partial void OnSelectedLeftVersionChanged(int? value)
    {
        RefreshLeftImages();
        OnPropertyChanged(nameof(LeftLabel));

        // In single dataset mode, the Right side uses the same version
        if (IsSingleDatasetMode)
        {
            OnPropertyChanged(nameof(EffectiveRightVersion));
            RefreshRightImages();
            OnPropertyChanged(nameof(RightLabel));
        }
    }

    partial void OnSelectedRightDatasetChanged(DatasetCardViewModel? value)
    {
        // If switching from temp to a real dataset on this side, check if we should exit external mode
        if (IsExternalMode && value is not null && !value.IsTemporary)
        {
            // Don't clear external mode here - just let this side use the real dataset
            // Only clear when both sides are no longer using temp
            if (SelectedLeftDataset is null || !SelectedLeftDataset.IsTemporary)
            {
                ClearExternalModeWithoutDatasetChange();
            }
        }

        LoadVersionsForDataset(value, RightVersionOptions);
        SelectedRightVersion = RightVersionOptions.FirstOrDefault();
        RefreshRightImages();
        OnPropertyChanged(nameof(RightLabel));
    }

    partial void OnSelectedRightVersionChanged(int? value)
    {
        RefreshRightImages();
        OnPropertyChanged(nameof(RightLabel));
    }

    partial void OnAssignSideChanged(CompareAssignSide value)
    {
        OnPropertyChanged(nameof(IsAssigningLeft));
        OnPropertyChanged(nameof(IsAssigningRight));
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

    partial void OnIsSingleDatasetModeChanged(bool value)
    {
        // Update computed properties
        OnPropertyChanged(nameof(EffectiveRightDataset));
        OnPropertyChanged(nameof(EffectiveRightVersion));

        // Refresh the Right images based on the new mode
        RefreshRightImages();
        OnPropertyChanged(nameof(RightLabel));
    }

    private void RefreshLeftImages()
    {
        List<ImageCompareItem> images;

        // Use external images if the selected dataset is the temp dataset
        if (SelectedLeftDataset?.IsTemporary == true && ExternalImages.Count > 0)
        {
            images = ExternalImages;
        }
        else
        {
            images = LoadImagesFromDataset(SelectedLeftDataset, SelectedLeftVersion);
        }

        SelectedLeftImage = images.FirstOrDefault();

        if (AssignSide == CompareAssignSide.Left)
        {
            RefreshFilmstrip();
        }
    }

    private void RefreshRightImages()
    {
        List<ImageCompareItem> images;

        var dataset = EffectiveRightDataset;
        var version = EffectiveRightVersion;

        // Use external images if the selected dataset is the temp dataset
        if (dataset?.IsTemporary == true && ExternalImages.Count > 0)
        {
            images = ExternalImages;
            // For right, default to second image if available
            SelectedRightImage = images.Count > 1 ? images[1] : images.FirstOrDefault();
        }
        else
        {
            images = LoadImagesFromDataset(dataset, version);
            SelectedRightImage = images.FirstOrDefault();
        }

        if (AssignSide == CompareAssignSide.Right)
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
        var left = SelectedLeftImage;
        var right = SelectedRightImage;
        SelectedLeftImage = right;
        SelectedRightImage = left;
    }

    public void AssignImage(ImageCompareItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (AssignSide == CompareAssignSide.Left)
        {
            SelectedLeftImage = item;
        }
        else
        {
            SelectedRightImage = item;
        }
    }

    public void AssignLeftImage(ImageCompareItem? item)
    {
        if (item is null) return;
        SelectedLeftImage = item;
    }

    public void AssignRightImage(ImageCompareItem? item)
    {
        if (item is null) return;
        SelectedRightImage = item;
    }

    private void RefreshFilmstrip()
    {
        FilmstripItems.Clear();

        var dataset = AssignSide == CompareAssignSide.Left ? SelectedLeftDataset : EffectiveRightDataset;
        var version = AssignSide == CompareAssignSide.Left ? SelectedLeftVersion : EffectiveRightVersion;

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
                item.IsSelectedLeft = item.ImagePath == SelectedLeftImage?.ImagePath;
                item.IsSelectedRight = item.ImagePath == SelectedRightImage?.ImagePath;
                FilmstripItems.Add(item);
            }
        }
    }

    private void UpdateSelectionFlags(ImageCompareItem? newSelection, bool isLeft)
    {
        foreach (var item in FilmstripItems)
        {
            if (isLeft)
            {
                item.IsSelectedLeft = item == newSelection || item.ImagePath == newSelection?.ImagePath;
            }
            else
            {
                item.IsSelectedRight = item == newSelection || item.ImagePath == newSelection?.ImagePath;
            }
        }
    }

    private static string BuildLabel(string? datasetName, int? version, string? imageName, string fallback)
    {
        var versionStr = version?.ToString() ?? "1";
        var dataset = string.IsNullOrWhiteSpace(datasetName) ? fallback : $"{datasetName} V{versionStr}";
        var image = string.IsNullOrWhiteSpace(imageName) ? "Unassigned" : imageName;
        return $"{dataset} / {image}";
    }
}

public enum CompareAssignSide
{
    Left,
    Right
}
