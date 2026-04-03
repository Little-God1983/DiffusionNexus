using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Autocropper;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Represents a target resolution option for downscaling.
/// </summary>
public record ResolutionOption(int? MaxSize, string DisplayName)
{
    public static ResolutionOption None { get; } = new(null, "No scaling (original size)");
    public static ResolutionOption Custom { get; } = new(null, "Custom");
}

/// <summary>
/// Represents a padding fill mode option for the UI.
/// </summary>
public record PaddingFillOption(PaddingFillMode Mode, string DisplayName)
{
    public static PaddingFillOption Black { get; } = new(PaddingFillMode.SolidColor, "Black");
    public static PaddingFillOption White { get; } = new(PaddingFillMode.White, "White");
    public static PaddingFillOption BlurFill { get; } = new(PaddingFillMode.BlurFill, "Blur Fill");
    public static PaddingFillOption Mirror { get; } = new(PaddingFillMode.Mirror, "Mirror");
}

/// <summary>
/// Converter that returns a description for the selected padding fill mode.
/// </summary>
public class PaddingFillDescriptionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PaddingFillMode mode)
        {
            return mode switch
            {
                PaddingFillMode.SolidColor => "Fills padding area with solid black.",
                PaddingFillMode.White => "Fills padding area with solid white.",
                PaddingFillMode.BlurFill => "Fills padding with a blurred/stretched version of the image. Great for maintaining visual context.",
                PaddingFillMode.Mirror => "Mirrors the image edges into the padding area for a seamless look.",
                _ => "Select a fill style for the padding area."
            };
        }
        return "Select a fill style for the padding area.";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converter that returns description text based on whether padding mode is active.
/// </summary>
public class BucketDescriptionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool usePadding && usePadding)
        {
            return "Images will be padded to the nearest bucket aspect ratio.";
        }
        return "Images will be cropped to the nearest bucket aspect ratio.";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converter that returns scale description text based on whether padding mode is active.
/// </summary>
public class ScaleDescriptionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool usePadding && usePadding)
        {
            return "Images will be padded first, then scaled down by longest side.";
        }
        return "Images will be cropped first, then scaled down by longest side.";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Represents a selectable aspect ratio bucket option.
/// </summary>
public partial class BucketOption : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public BucketDefinition Bucket { get; }
    public string DisplayName => Bucket.Name;
    public double Ratio => Bucket.Ratio;

    public BucketOption(BucketDefinition bucket)
    {
        Bucket = bucket;
    }
}

/// <summary>
/// ViewModel for the Batch Crop/Scale tab.
/// Handles batch cropping images to standard aspect ratio buckets for LoRA training.
/// Supports both folder-based and dataset-based source selection.
/// Supports both cropping (remove pixels) and padding (add canvas) fit modes.
/// </summary>
public partial class BatchCropScaleTabViewModel : ObservableObject, IDisposable
{
    private readonly IImageCropperService _cropperService;
    private readonly IDatasetState? _state;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly IAppSettingsService? _settingsService;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;
    private DatasetCardViewModel? _tempDataset;
    private string? _tempStagingDir;
    private string? _singleImagePath;
    private bool _isSingleImageMode;

    #region Single Image Properties

    /// <summary>
    /// Whether to crop/scale a single image instead of a dataset.
    /// </summary>
    public bool IsSingleImageMode
    {
        get => _isSingleImageMode;
        set
        {
            if (SetProperty(ref _isSingleImageMode, value))
            {
                if (value) SelectedDataset = null;
                else SingleImagePath = null;
                OnPropertyChanged(nameof(IsDatasetMode));
                OnPropertyChanged(nameof(HasSourceSelected));
                OnPropertyChanged(nameof(CanStart));
                StartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether dataset mode is active (inverse of single image mode).
    /// Used for AXAML visibility bindings.
    /// </summary>
    public bool IsDatasetMode => !IsSingleImageMode;

    /// <summary>
    /// Path to a single image for crop/scale.
    /// </summary>
    public string? SingleImagePath
    {
        get => _singleImagePath;
        set
        {
            if (SetProperty(ref _singleImagePath, value))
            {
                if (!string.IsNullOrEmpty(value)) IsSingleImageMode = true;
                OnPropertyChanged(nameof(SingleImageName));
                OnPropertyChanged(nameof(HasSingleImage));
                OnPropertyChanged(nameof(HasSourceSelected));
                OnPropertyChanged(nameof(CanStart));
                StartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Display name for the selected single image.
    /// </summary>
    public string? SingleImageName => Path.GetFileName(SingleImagePath);

    /// <summary>
    /// Whether a single image is currently loaded.
    /// </summary>
    public bool HasSingleImage => !string.IsNullOrEmpty(SingleImagePath);

    #endregion

    #region Dataset Selection Properties (Source)

    private DatasetCardViewModel? _selectedDataset;
    private EditorVersionItem? _selectedVersion;

    /// <summary>
    /// Selected dataset for processing.
    /// Selecting a dataset clears the source folder and exits single image mode.
    /// </summary>
    public DatasetCardViewModel? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (SetProperty(ref _selectedDataset, value))
            {
                // Clear source folder when dataset is selected
                if (value is not null && !string.IsNullOrWhiteSpace(SourceFolder))
                {
                    SourceFolder = string.Empty;
                }
                if (value is not null)
                {
                    IsSingleImageMode = false;
                    if (!value.IsTemporary)
                    {
                        ClearGallerySelection();
                    }
                }
                _ = LoadDatasetVersionsAsync();
                UpdateNextVersionNumber();
                OnPropertyChanged(nameof(HasSourceSelected));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanIncrementVersion));
                StartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Selected version within the dataset.
    /// </summary>
    public EditorVersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                UpdateDatasetImageCount();
                OnPropertyChanged(nameof(HasSourceSelected));
                OnPropertyChanged(nameof(CanStart));
                StartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    #endregion

    #region Target Version Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverwriteMode))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(EffectiveTargetFolder))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _useIncrementVersion = true;

    [ObservableProperty]
    private int _nextVersionNumber;

    /// <summary>
    /// Whether to copy captions when creating a new version.
    /// Default is true to keep LoRA and caption data together.
    /// </summary>
    [ObservableProperty]
    private bool _copyCaptionsWithNewVersion = true;

    /// <summary>
    /// True if a dataset is selected (enables version increment option).
    /// </summary>
    public bool CanIncrementVersion => SelectedDataset is not null;

    /// <summary>
    /// Display text for the increment version button.
    /// </summary>
    public string IncrementVersionDisplayText => $"Create V{NextVersionNumber}";

    #endregion

    #region Fit Mode Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPadMode))]
    private bool _usePadding;

    /// <summary>
    /// True when padding mode is selected (shows padding options).
    /// </summary>
    public bool IsPadMode => UsePadding;

    [ObservableProperty]
    private PaddingFillOption _selectedPaddingFill = PaddingFillOption.Black;

    /// <summary>
    /// Available padding fill options.
    /// </summary>
    public ObservableCollection<PaddingFillOption> PaddingFillOptions { get; } =
    [
        PaddingFillOption.Black,
        PaddingFillOption.White,
        PaddingFillOption.BlurFill,
        PaddingFillOption.Mirror
    ];

    #endregion

    #region Collections

    /// <summary>
    /// Collection of all datasets for the dropdown.
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> Datasets => _state?.Datasets ?? [];

    /// <summary>
    /// Version items for the version dropdown.
    /// </summary>
    public ObservableCollection<EditorVersionItem> VersionItems { get; } = [];

    public ObservableCollection<BucketOption> BucketOptions { get; } = [];

    public ObservableCollection<ResolutionOption> ResolutionOptions { get; } =
    [
        ResolutionOption.None,
        new(512, "512 px"),
        new(768, "768 px"),
        new(1024, "1024 px"),
        new(1536, "1536 px"),
        new(2048, "2048 px"),
        ResolutionOption.Custom
    ];

    #endregion

    #region Observable Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(HasSourceSelected))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _sourceFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverwriteMode))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(EffectiveTargetFolder))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _targetFolder = string.Empty;

    [ObservableProperty]
    private bool _isTargetFolderNotEmpty;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _imageFiles;

    [ObservableProperty]
    private int _processedCount;

    [ObservableProperty]
    private string _currentFile = string.Empty;

    [ObservableProperty]
    private string _currentBucket = string.Empty;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _overwriteConfirmed;

    [ObservableProperty]
    private bool _skipUnchanged;

    [ObservableProperty]
    private ResolutionOption _selectedResolution = ResolutionOption.None;

    [ObservableProperty]
    private string _customResolution = string.Empty;

    #endregion

    #region Computed Properties

    public BucketDefinition[] SelectedBuckets => BucketOptions
        .Where(b => b.IsSelected)
        .Select(b => b.Bucket)
        .ToArray();

    public bool HasSelectedBuckets => BucketOptions.Any(b => b.IsSelected);

    /// <summary>
    /// True if no target is specified (overwrite mode).
    /// </summary>
    public bool IsOverwriteMode => 
        string.IsNullOrWhiteSpace(TargetFolder) && !UseIncrementVersion;

    /// <summary>
    /// True if a valid source is selected: single image, source folder, or dataset+version.
    /// </summary>
    public bool HasSourceSelected =>
        (IsSingleImageMode && HasSingleImage) ||
        !string.IsNullOrWhiteSpace(SourceFolder) ||
        (SelectedDataset is not null && SelectedVersion is not null);

    /// <summary>
    /// Gets the effective source folder path (either manual or from dataset).
    /// </summary>
    public string EffectiveSourceFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SourceFolder))
                return SourceFolder;

            if (SelectedDataset is not null && SelectedVersion is not null)
                return SelectedDataset.GetVersionFolderPath(SelectedVersion.Version);

            return string.Empty;
        }
    }

    /// <summary>
    /// Gets the effective target folder path (manual folder, new version folder, or null for overwrite).
    /// </summary>
    public string? EffectiveTargetFolder
    {
        get
        {
            if (UseIncrementVersion && SelectedDataset is not null)
                return SelectedDataset.GetVersionFolderPath(NextVersionNumber);

            if (!string.IsNullOrWhiteSpace(TargetFolder))
                return TargetFolder;

            return null;
        }
    }

    public bool CanStart =>
        HasSourceSelected &&
        HasSelectedBuckets &&
        (IsSingleImageMode || !IsOverwriteMode || OverwriteConfirmed) &&
        !IsProcessing;

    public double ProgressPercentage => ImageFiles > 0
        ? (double)ProcessedCount / ImageFiles * 100
        : 0;

    #endregion

    #region Dialog Service

    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Opens a file dialog to select a single image for crop/scale.
    /// </summary>
    public IAsyncRelayCommand SelectSingleImageCommand { get; }

    /// <summary>
    /// Clears the currently loaded single image.
    /// </summary>
    public IRelayCommand ClearSingleImageCommand { get; }

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new BatchCropScaleTabViewModel with dataset state support.
    /// </summary>
    public BatchCropScaleTabViewModel(IDatasetState? state = null, IDatasetEventAggregator? eventAggregator = null, IAppSettingsService? settingsService = null)
    {
        _state = state;
        _eventAggregator = eventAggregator;
        _settingsService = settingsService;
        _cropperService = new ImageCropperService();

        SelectSingleImageCommand = new AsyncRelayCommand(SelectSingleImageAsync);
        ClearSingleImageCommand = new RelayCommand(() => SingleImagePath = null);

        // Subscribe to events for dataset/version creation
        if (_eventAggregator is not null)
        {
            _eventAggregator.DatasetCreated += OnDatasetCreated;
            _eventAggregator.VersionCreated += OnVersionCreated;
            _eventAggregator.ImageAdded += OnImageAdded;
        }

        // Load default buckets
        InitializeBuckets();
    }

    private void InitializeBuckets()
    {
        // Default aspect ratio buckets for LoRA training
        var defaultBuckets = new[]
        {
            new BucketDefinition { Name = "16:9", Width = 16, Height = 9 },
            new BucketDefinition { Name = "9:16", Width = 9, Height = 16 },
            new BucketDefinition { Name = "1:1", Width = 1, Height = 1 },
            new BucketDefinition { Name = "4:3", Width = 4, Height = 3 },
            new BucketDefinition { Name = "3:4", Width = 3, Height = 4 },
            new BucketDefinition { Name = "5:4", Width = 5, Height = 4 },
            new BucketDefinition { Name = "4:5", Width = 4, Height = 5 }
        };

        foreach (var bucket in defaultBuckets)
        {
            BucketOptions.Add(new BucketOption(bucket));
        }
    }

    #endregion

    #region Partial Methods

    partial void OnCustomResolutionChanged(string value)
    {
        if (SelectedResolution == ResolutionOption.Custom && int.TryParse(value, out _))
        {
            OnPropertyChanged(nameof(CanStart));
        }
    }

    partial void OnSelectedResolutionChanged(ResolutionOption value)
    {
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnSourceFolderChanged(string value)
    {
        // Clear dataset selection and single image mode when source folder is specified
        if (!string.IsNullOrWhiteSpace(value))
        {
            _isSingleImageMode = false;
            _singleImagePath = null;
            OnPropertyChanged(nameof(IsSingleImageMode));
            OnPropertyChanged(nameof(IsDatasetMode));
            OnPropertyChanged(nameof(SingleImagePath));
            OnPropertyChanged(nameof(HasSingleImage));

            _selectedDataset = null;
            _selectedVersion = null;
            VersionItems.Clear();
            OnPropertyChanged(nameof(SelectedDataset));
            OnPropertyChanged(nameof(SelectedVersion));
            
            // Also clear increment version option since no dataset is selected
            UseIncrementVersion = false;
            OnPropertyChanged(nameof(CanIncrementVersion));
        }

        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            UpdateFolderMetadata();
        }
        else if (string.IsNullOrWhiteSpace(value) && SelectedDataset is null)
        {
            TotalFiles = 0;
            ImageFiles = 0;
        }
    }

    partial void OnTargetFolderChanged(string value)
    {
        // Clear increment version when manual folder is specified
        if (!string.IsNullOrWhiteSpace(value))
        {
            UseIncrementVersion = false;
        }
        
        CheckTargetFolderEmpty();
        OnPropertyChanged(nameof(IsOverwriteMode));
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnUseIncrementVersionChanged(bool value)
    {
        // Clear manual target folder when increment version is selected
        if (value && !string.IsNullOrWhiteSpace(TargetFolder))
        {
            _targetFolder = string.Empty;
            OnPropertyChanged(nameof(TargetFolder));
        }
        
        OnPropertyChanged(nameof(IsOverwriteMode));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(EffectiveTargetFolder));
        OnPropertyChanged(nameof(IncrementVersionDisplayText));
    }

    #endregion

    #region Dataset Methods

    /// <summary>
    /// Handles dataset created events - notifies UI to refresh dataset dropdown.
    /// </summary>
    private void OnDatasetCreated(object? sender, DatasetCreatedEventArgs e)
    {
        // The Datasets collection is shared via IDatasetState, so it's already updated.
        // We just need to notify the UI that the collection may have changed.
        OnPropertyChanged(nameof(Datasets));
    }

    /// <summary>
    /// Handles version created events - refreshes version dropdown if viewing the affected dataset.
    /// </summary>
    private async void OnVersionCreated(object? sender, VersionCreatedEventArgs e)
    {
        // If we're currently viewing this dataset, refresh the version list
        if (_selectedDataset is not null &&
            string.Equals(_selectedDataset.FolderPath, e.Dataset.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            await LoadDatasetVersionsAsync();
            UpdateNextVersionNumber();
        }
    }

    /// <summary>
    /// Handles image added events - refreshes version dropdown and image count when images are added.
    /// </summary>
    private async void OnImageAdded(object? sender, ImageAddedEventArgs e)
    {
        // If we're currently viewing this dataset, refresh the version list for updated counts
        if (_selectedDataset is not null &&
            string.Equals(_selectedDataset.FolderPath, e.Dataset.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            var currentVersion = _selectedVersion?.Version;
            await LoadDatasetVersionsAsync();
            
            // Re-select the version if one was selected
            if (currentVersion.HasValue)
            {
                var versionToReselect = VersionItems.FirstOrDefault(v => v.Version == currentVersion.Value);
                if (versionToReselect is not null)
                {
                    _selectedVersion = versionToReselect;
                    OnPropertyChanged(nameof(SelectedVersion));
                }
            }
            
            UpdateDatasetImageCount();
        }
    }

    /// <summary>
    /// Preselects a dataset and version for processing.
    /// Called when navigating from the Dataset Management view.
    /// </summary>
    public void PreselectDataset(DatasetCardViewModel dataset, int version)
    {
        // Find the matching dataset in our collection
        var matchingDataset = Datasets.FirstOrDefault(d => 
            string.Equals(d.FolderPath, dataset.FolderPath, StringComparison.OrdinalIgnoreCase));

        if (matchingDataset is not null)
        {
            SelectedDataset = matchingDataset;
        }
        else
        {
            // Dataset not in shared state yet - set it directly
            SelectedDataset = dataset;
        }

        // Select the version after dataset is set (allows version items to load)
        _ = SelectVersionAsync(version);

        StatusMessage = $"Dataset '{dataset.Name}' V{version} loaded for processing";
    }

    /// <summary>
    /// Opens a file dialog to select a single image for crop/scale.
    /// </summary>
    private async Task SelectSingleImageAsync()
    {
        if (DialogService is null) return;

        var result = await DialogService.ShowOpenFileDialogAsync("Select Image", null);
        if (!string.IsNullOrEmpty(result))
        {
            SingleImagePath = result;
        }
    }

    /// <summary>
    /// Loads a single image for crop/scale. Can be called from drag-drop or external navigation.
    /// </summary>
    public void LoadSingleImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        ClearGallerySelection();
        SingleImagePath = imagePath;
    }

    /// <summary>
    /// Builds the output path for a single image crop/scale.
    /// Saves next to the original as {name}_cropped{ext}.
    /// </summary>
    private static string GetSingleImageOutputPath(string originalPath)
    {
        var dir = Path.GetDirectoryName(originalPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        return Path.Combine(dir, $"{name}_cropped{ext}");
    }

    /// <summary>
    /// Loads multiple images as a temporary batch for crop/scale.
    /// Stages images into a temp directory and injects a "Gallery Selection" dataset into the combo box.
    /// </summary>
    public void LoadTemporaryImages(IReadOnlyList<string> imagePaths)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);

        var validPaths = imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (validPaths.Count == 0) return;

        // Stage images into a temp directory so the crop service can process them as a folder
        CleanupTempStagingDir();
        _tempStagingDir = Path.Combine(Path.GetTempPath(), "DiffusionNexus", "crop-gallery", Guid.NewGuid().ToString("N"));
        var versionDir = Path.Combine(_tempStagingDir, "V1");
        Directory.CreateDirectory(versionDir);

        foreach (var src in validPaths)
        {
            var dest = Path.Combine(versionDir, Path.GetFileName(src));
            // Avoid name collisions by appending a counter
            if (File.Exists(dest))
            {
                var name = Path.GetFileNameWithoutExtension(src);
                var ext = Path.GetExtension(src);
                dest = Path.Combine(versionDir, $"{name}_{Guid.NewGuid():N}{ext}");
            }
            File.Copy(src, dest);
        }

        SourceFolder = string.Empty;

        _tempDataset = new DatasetCardViewModel
        {
            Name = "Gallery Selection",
            FolderPath = _tempStagingDir,
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = validPaths.Count,
            TotalImageCountAllVersions = validPaths.Count,
            IsTemporary = true
        };

        var existingTemp = Datasets.FirstOrDefault(d => d.IsTemporary);
        if (existingTemp is not null)
        {
            Datasets.Remove(existingTemp);
        }
        Datasets.Insert(0, _tempDataset);

        SelectedDataset = _tempDataset;
        SelectedVersion = VersionItems.FirstOrDefault();

        // Auto-enable "Create New Version" so ConvertTempDatasetToPersistentAsync is triggered
        UseIncrementVersion = true;

        StatusMessage = $"Gallery Selection: {validPaths.Count} image(s) staged for processing.";
    }

    /// <summary>
    /// Removes the temporary "Gallery Selection" dataset from the combo box.
    /// </summary>
    private void ClearGallerySelection()
    {
        if (_tempDataset is null) return;

        Datasets.Remove(_tempDataset);
        _tempDataset = null;
        CleanupTempStagingDir();
    }

    /// <summary>
    /// Cleans up the temporary staging directory used for gallery selections.
    /// </summary>
    private void CleanupTempStagingDir()
    {
        if (_tempStagingDir is null) return;

        try
        {
            if (Directory.Exists(_tempStagingDir))
            {
                Directory.Delete(_tempStagingDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; ignore failures
        }
        _tempStagingDir = null;
    }

    private async Task SelectVersionAsync(int version)
    {
        // Small delay to allow version loading to complete
        await Task.Delay(150);
        
        var matchingVersion = VersionItems.FirstOrDefault(v => v.Version == version);
        if (matchingVersion is not null)
        {
            SelectedVersion = matchingVersion;
        }
    }

    /// <summary>
    /// Updates the next version number based on the selected dataset.
    /// </summary>
    private void UpdateNextVersionNumber()
    {
        if (_selectedDataset is not null)
        {
            NextVersionNumber = _selectedDataset.GetNextVersionNumber();
            OnPropertyChanged(nameof(IncrementVersionDisplayText));
        }
        else
        {
            NextVersionNumber = 1;
        }
    }

    /// <summary>
    /// Loads the available versions for the selected dataset.
    /// </summary>
    private async Task LoadDatasetVersionsAsync()
    {
        VersionItems.Clear();
        _selectedVersion = null;
        OnPropertyChanged(nameof(SelectedVersion));

        if (_selectedDataset is null) return;

        // Temp dataset: create a single version item with stored count
        if (_selectedDataset.IsTemporary)
        {
            VersionItems.Add(EditorVersionItem.Create(1, _selectedDataset.ImageCount));
            await Task.CompletedTask;
            return;
        }

        try
        {
            var versionNumbers = _selectedDataset.GetAllVersionNumbers();

            foreach (var version in versionNumbers)
            {
                var versionPath = _selectedDataset.GetVersionFolderPath(version);
                var imageCount = 0;

                if (Directory.Exists(versionPath))
                {
                    imageCount = Directory.EnumerateFiles(versionPath)
                        .Count(f => MediaFileExtensions.IsImageFile(f) && !MediaFileExtensions.IsVideoThumbnailFile(f));
                }

                VersionItems.Add(EditorVersionItem.Create(version, imageCount));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading versions: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Updates the image count when a dataset version is selected.
    /// </summary>
    private void UpdateDatasetImageCount()
    {
        if (_selectedDataset is null || _selectedVersion is null)
        {
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                TotalFiles = 0;
                ImageFiles = 0;
            }
            return;
        }

        // Temp dataset: use stored count
        if (_selectedDataset.IsTemporary)
        {
            ImageFiles = _selectedDataset.ImageCount;
            TotalFiles = _selectedDataset.ImageCount;
            StatusMessage = $"Gallery Selection: {ImageFiles} images ready for processing";
            return;
        }

        try
        {
            var versionPath = _selectedDataset.GetVersionFolderPath(_selectedVersion.Version);
            if (Directory.Exists(versionPath))
            {
                var allFiles = Directory.GetFiles(versionPath, "*", SearchOption.TopDirectoryOnly);
                var imageFilesList = allFiles
                    .Where(f => MediaFileExtensions.IsImageFile(f) && !MediaFileExtensions.IsVideoThumbnailFile(f))
                    .ToArray();

                TotalFiles = allFiles.Length;
                ImageFiles = imageFilesList.Length;
                StatusMessage = $"Found {ImageFiles} images in dataset version {_selectedVersion.Version}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning dataset: {ex.Message}";
            TotalFiles = 0;
            ImageFiles = 0;
        }
    }

    /// <summary>
    /// Clears the dataset selection.
    /// </summary>
    [RelayCommand]
    private void ClearDatasetSelection()
    {
        SelectedDataset = null;
        SelectedVersion = null;
        VersionItems.Clear();
        UseIncrementVersion = false;
        TotalFiles = 0;
        ImageFiles = 0;
        StatusMessage = string.Empty;
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void SelectAllBuckets()
    {
        foreach (var bucket in BucketOptions)
        {
            bucket.IsSelected = true;
        }
        OnPropertyChanged(nameof(HasSelectedBuckets));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void DeselectAllBuckets()
    {
        foreach (var bucket in BucketOptions)
        {
            bucket.IsSelected = false;
        }
        OnPropertyChanged(nameof(HasSelectedBuckets));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsProcessing) return;

        IsProcessing = true;
        ProcessedCount = 0;
        SuccessCount = 0;
        FailedCount = 0;
        SkippedCount = 0;
        StatusMessage = "Processing...";

        _cancellationTokenSource = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Determine max resolution (shared by single image and dataset modes)
            int? maxLongestSide = null;
            if (SelectedResolution != ResolutionOption.None)
            {
                if (SelectedResolution == ResolutionOption.Custom &&
                    int.TryParse(CustomResolution, out var customSize) && customSize > 0)
                {
                    maxLongestSide = customSize;
                }
                else if (SelectedResolution.MaxSize.HasValue)
                {
                    maxLongestSide = SelectedResolution.MaxSize;
                }
            }

            // Configure fit mode and padding options (shared)
            var fitMode = UsePadding ? FitMode.Pad : FitMode.Crop;
            var paddingOptions = UsePadding 
                ? new PaddingOptions 
                { 
                    FillMode = SelectedPaddingFill.Mode,
                    FillColor = SelectedPaddingFill.Mode == PaddingFillMode.SolidColor ? "#FF000000" : "#FFFFFFFF"
                }
                : null;

            // --- Single Image Mode ---
            if (IsSingleImageMode)
            {
                if (string.IsNullOrEmpty(SingleImagePath) || !File.Exists(SingleImagePath))
                {
                    StatusMessage = "No image selected.";
                    return;
                }

                var outputPath = GetSingleImageOutputPath(SingleImagePath);
                var outputDir = Path.GetDirectoryName(outputPath) ?? ".";

                // Stage the single image in a temp directory for the crop service
                var tempDir = Path.Combine(Path.GetTempPath(), "DiffusionNexus", "crop-single", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                var stagedFile = Path.Combine(tempDir, Path.GetFileName(SingleImagePath));
                File.Copy(SingleImagePath, stagedFile);

                var progress = new Progress<CropProgress>(OnProgressUpdate);
                ImageFiles = 1;

                var result = await _cropperService.ProcessImagesAsync(
                    tempDir,
                    outputDir,
                    SelectedBuckets,
                    maxLongestSide,
                    skipUnchanged: false,
                    fitMode,
                    paddingOptions,
                    progress,
                    _cancellationTokenSource.Token);

                SuccessCount = result.SuccessCount;
                FailedCount = result.FailedCount;
                SkippedCount = result.SkippedCount;

                // Rename the output file to the _cropped name if the service wrote it with the original name
                var serviceOutput = Path.Combine(outputDir, Path.GetFileName(SingleImagePath));
                if (File.Exists(serviceOutput) && serviceOutput != outputPath)
                {
                    File.Move(serviceOutput, outputPath, overwrite: true);
                }

                var modeInfo = UsePadding ? " (padded)" : " (cropped)";
                StatusMessage = $"Done{modeInfo} – saved to {Path.GetFileName(outputPath)}";

                // Cleanup temp staging
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
                return;
            }

            // --- Dataset Mode ---

            // Create the target version folder if using increment version
            if (UseIncrementVersion && SelectedDataset is not null)
            {
                // For temporary datasets (gallery selection), create a real persistent dataset first
                if (SelectedDataset.IsTemporary)
                {
                    var persistentDataset = await ConvertTempDatasetToPersistentAsync();
                    if (persistentDataset is null)
                    {
                        // Conversion failed (e.g., storage path not configured)
                        return;
                    }
                }

                var newVersionPath = SelectedDataset.GetVersionFolderPath(NextVersionNumber);
                Directory.CreateDirectory(newVersionPath);

                // Record the branch in the dataset metadata
                if (SelectedVersion is not null)
                {
                    SelectedDataset.RecordBranch(NextVersionNumber, SelectedVersion.Version);
                    SelectedDataset.SaveMetadata();
                }
            }

            var datasetProgress = new Progress<CropProgress>(OnProgressUpdate);

            // Use effective source and target folders
            var datasetResult = await _cropperService.ProcessImagesAsync(
                EffectiveSourceFolder,
                EffectiveTargetFolder,
                SelectedBuckets,
                maxLongestSide,
                SkipUnchanged,
                fitMode,
                paddingOptions,
                datasetProgress,
                _cancellationTokenSource.Token);

            SuccessCount = datasetResult.SuccessCount;
            FailedCount = datasetResult.FailedCount;
            SkippedCount = datasetResult.SkippedCount;

            // Copy captions if creating a new version and the option is enabled
            var captionsCopied = 0;
            if (UseIncrementVersion && SelectedDataset is not null && CopyCaptionsWithNewVersion)
            {
                captionsCopied = await CopyCaptionsToNewVersionAsync(
                    EffectiveSourceFolder,
                    EffectiveTargetFolder!,
                    _cancellationTokenSource.Token);
            }

            var datasetModeInfo = UsePadding ? " (padded)" : " (cropped)";
            var targetInfo = UseIncrementVersion 
                ? $" to V{NextVersionNumber}" 
                : "";
            var captionInfo = captionsCopied > 0 ? $", {captionsCopied} captions copied" : "";
            StatusMessage = $"Completed{targetInfo}{datasetModeInfo}: {SuccessCount} processed, {SkippedCount} skipped, {FailedCount} failed{captionInfo}";

            // If we created a new version, update the dataset model and publish events
            if (UseIncrementVersion && SelectedDataset is not null)
            {
                var branchedFrom = SelectedVersion?.Version;
                FinalizeVersionCreation(NextVersionNumber, branchedFrom);
                UpdateNextVersionNumber();
                _ = LoadDatasetVersionsAsync(); // Refresh version list
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            stopwatch.Stop();
            ElapsedTime = stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling...";
    }

    [RelayCommand]
    private void Refresh()
    {
        if (!string.IsNullOrWhiteSpace(SourceFolder) && Directory.Exists(SourceFolder))
        {
            UpdateFolderMetadata();
        }
        else if (SelectedDataset is not null && SelectedVersion is not null)
        {
            UpdateDatasetImageCount();
        }
        
        // Also refresh next version number
        UpdateNextVersionNumber();
    }

    [RelayCommand]
    private void ClearTargetFolder()
    {
        TargetFolder = string.Empty;
        UseIncrementVersion = false;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Copies caption files (.txt) from the source folder to the target folder.
    /// Only copies captions for images that exist in the target folder (i.e., were processed).
    /// </summary>
    /// <param name="sourceFolder">The source folder containing caption files.</param>
    /// <param name="targetFolder">The target folder to copy captions to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of captions copied.</returns>
    private static async Task<int> CopyCaptionsToNewVersionAsync(
        string sourceFolder,
        string targetFolder,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceFolder) || !Directory.Exists(targetFolder))
            return 0;

        var copied = 0;

        // Get all image files in the target folder to know which captions to copy
        var targetImages = Directory.EnumerateFiles(targetFolder)
            .Where(MediaFileExtensions.IsImageFile)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find and copy matching caption files from source
        var captionFiles = Directory.EnumerateFiles(sourceFolder)
            .Where(MediaFileExtensions.IsCaptionFile);

        foreach (var captionFile in captionFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var baseName = Path.GetFileNameWithoutExtension(captionFile);
            
            // Only copy if the corresponding image was processed to the target
            if (targetImages.Contains(baseName))
            {
                var fileName = Path.GetFileName(captionFile);
                var targetCaptionPath = Path.Combine(targetFolder, fileName);

                // Don't overwrite existing captions
                if (!File.Exists(targetCaptionPath))
                {
                    await Task.Run(() => File.Copy(captionFile, targetCaptionPath), cancellationToken);
                    copied++;
                }
            }
        }

        return copied;
    }

    /// <summary>
    /// Converts a temporary (gallery selection) dataset into a persistent dataset
    /// stored in the configured dataset storage path. Copies source images into V1
    /// of the new dataset so that version increment processing targets a real folder.
    /// </summary>
    /// <returns>The persistent dataset, or null if creation failed.</returns>
    private async Task<DatasetCardViewModel?> ConvertTempDatasetToPersistentAsync()
    {
        if (_settingsService is null || SelectedDataset is null || !SelectedDataset.IsTemporary)
            return null;

        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath) || !Directory.Exists(settings.DatasetStoragePath))
        {
            StatusMessage = "Dataset storage path is not configured. Please set it in Settings before creating a new version from gallery images.";
            return null;
        }

        // Auto-generate a unique dataset name
        var baseName = $"Gallery Crop {DateTime.Now:yyyy-MM-dd HH-mm}";
        var datasetPath = Path.Combine(settings.DatasetStoragePath, baseName);

        // Ensure unique name
        var counter = 2;
        while (Directory.Exists(datasetPath))
        {
            datasetPath = Path.Combine(settings.DatasetStoragePath, $"{baseName} ({counter++})");
        }

        var datasetName = Path.GetFileName(datasetPath);
        Directory.CreateDirectory(datasetPath);

        var v1Path = Path.Combine(datasetPath, "V1");
        Directory.CreateDirectory(v1Path);

        // Copy staged images from temp V1 to the real V1
        var tempV1 = SelectedDataset.GetVersionFolderPath(1);
        if (Directory.Exists(tempV1))
        {
            foreach (var file in Directory.EnumerateFiles(tempV1))
            {
                var destFile = Path.Combine(v1Path, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }
        }

        var newDataset = new DatasetCardViewModel
        {
            Name = datasetName,
            FolderPath = datasetPath,
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = SelectedDataset.ImageCount,
            TotalImageCountAllVersions = SelectedDataset.ImageCount
        };
        newDataset.SaveMetadata();

        // Remove the temp dataset and register the persistent one
        Datasets.Remove(SelectedDataset);
        ClearGallerySelection();

        if (!Datasets.Contains(newDataset))
        {
            Datasets.Add(newDataset);
        }

        _eventAggregator?.PublishDatasetCreated(new DatasetCreatedEventArgs
        {
            Dataset = newDataset
        });

        // Select the new persistent dataset
        SelectedDataset = newDataset;
        await LoadDatasetVersionsAsync();
        SelectedVersion = VersionItems.FirstOrDefault();
        UpdateNextVersionNumber();

        return newDataset;
    }

    /// <summary>
    /// Updates the dataset model and publishes events after a new version is successfully created.
    /// </summary>
    private void FinalizeVersionCreation(int newVersion, int? branchedFromVersion)
    {
        if (SelectedDataset is null) return;

        SelectedDataset.CurrentVersion = newVersion;
        SelectedDataset.IsVersionedStructure = true;
        SelectedDataset.TotalVersions = SelectedDataset.GetAllVersionNumbers().Count();
        SelectedDataset.RefreshImageInfo();

        if (_eventAggregator is not null)
        {
            _eventAggregator.PublishVersionCreated(new VersionCreatedEventArgs
            {
                Dataset = SelectedDataset,
                NewVersion = newVersion,
                BranchedFromVersion = branchedFromVersion ?? 1
            });
        }
    }

    private void CheckTargetFolderEmpty()
    {
        if (string.IsNullOrWhiteSpace(TargetFolder) || !Directory.Exists(TargetFolder))
        {
            IsTargetFolderNotEmpty = false;
            return;
        }

        IsTargetFolderNotEmpty = Directory.EnumerateFileSystemEntries(TargetFolder).Any();
    }

    private void UpdateFolderMetadata()
    {
        try
        {
            var result = _cropperService.ScanFolder(SourceFolder);
            TotalFiles = result.TotalFiles;
            ImageFiles = result.ImageFiles;
            StatusMessage = $"Found {ImageFiles} images in {TotalFiles} files";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning folder: {ex.Message}";
            TotalFiles = 0;
            ImageFiles = 0;
        }
    }

    private void OnProgressUpdate(CropProgress progress)
    {
        ProcessedCount = progress.ProcessedCount;
        CurrentFile = Path.GetFileName(progress.CurrentFile);
        CurrentBucket = progress.CurrentBucket?.Name ?? string.Empty;
        OnPropertyChanged(nameof(ProgressPercentage));
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Unsubscribe from events to prevent memory leaks
            if (_eventAggregator is not null)
            {
                _eventAggregator.DatasetCreated -= OnDatasetCreated;
                _eventAggregator.VersionCreated -= OnVersionCreated;
                _eventAggregator.ImageAdded -= OnImageAdded;
            }

            ClearGallerySelection();
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
