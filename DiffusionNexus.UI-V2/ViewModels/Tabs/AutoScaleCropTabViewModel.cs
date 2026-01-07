using System.Collections.ObjectModel;
using System.Diagnostics;
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
/// ViewModel for the Auto Scale/Crop tab.
/// Handles batch cropping images to standard aspect ratio buckets for LoRA training.
/// Supports both folder-based and dataset-based source selection.
/// </summary>
public partial class AutoScaleCropTabViewModel : ObservableObject, IDisposable
{
    private readonly IImageCropperService _cropperService;
    private readonly IDatasetState? _state;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;

    #region Dataset Selection Properties

    private DatasetCardViewModel? _selectedDataset;
    private EditorVersionItem? _selectedVersion;

    /// <summary>
    /// Selected dataset for processing.
    /// Selecting a dataset clears the source folder.
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
                    _sourceFolder = string.Empty;
                    OnPropertyChanged(nameof(SourceFolder));
                }
                _ = LoadDatasetVersionsAsync();
                OnPropertyChanged(nameof(HasSourceSelected));
                OnPropertyChanged(nameof(CanStart));
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

    public bool IsOverwriteMode => string.IsNullOrWhiteSpace(TargetFolder);

    /// <summary>
    /// True if either a source folder is specified OR a dataset+version is selected.
    /// </summary>
    public bool HasSourceSelected =>
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

    public bool CanStart =>
        HasSourceSelected &&
        HasSelectedBuckets &&
        (!IsOverwriteMode || OverwriteConfirmed) &&
        !IsProcessing;

    public double ProgressPercentage => ImageFiles > 0
        ? (double)ProcessedCount / ImageFiles * 100
        : 0;

    #endregion

    #region Dialog Service

    public IDialogService? DialogService { get; set; }

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new AutoScaleCropTabViewModel with dataset state support.
    /// </summary>
    public AutoScaleCropTabViewModel(IDatasetState? state = null)
    {
        _state = state;
        _cropperService = new ImageCropperService();

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
        // Clear dataset selection when source folder is specified
        if (!string.IsNullOrWhiteSpace(value))
        {
            _selectedDataset = null;
            _selectedVersion = null;
            VersionItems.Clear();
            OnPropertyChanged(nameof(SelectedDataset));
            OnPropertyChanged(nameof(SelectedVersion));
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
        CheckTargetFolderEmpty();
        OnPropertyChanged(nameof(IsOverwriteMode));
        OnPropertyChanged(nameof(CanStart));
    }

    #endregion

    #region Dataset Methods

    /// <summary>
    /// Loads the available versions for the selected dataset.
    /// </summary>
    private async Task LoadDatasetVersionsAsync()
    {
        VersionItems.Clear();
        _selectedVersion = null;
        OnPropertyChanged(nameof(SelectedVersion));

        if (_selectedDataset is null) return;

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

            // Auto-select the first version if available
            if (VersionItems.Count > 0)
            {
                SelectedVersion = VersionItems[0];
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
            // Determine max resolution
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

            var progress = new Progress<CropProgress>(OnProgressUpdate);

            // Use effective source folder (manual or from dataset)
            var result = await _cropperService.ProcessImagesAsync(
                EffectiveSourceFolder,
                string.IsNullOrWhiteSpace(TargetFolder) ? null : TargetFolder,
                SelectedBuckets,
                maxLongestSide,
                SkipUnchanged,
                progress,
                _cancellationTokenSource.Token);

            SuccessCount = result.SuccessCount;
            FailedCount = result.FailedCount;
            SkippedCount = result.SkippedCount;
            StatusMessage = $"Completed: {SuccessCount} processed, {SkippedCount} skipped, {FailedCount} failed";
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
    }

    [RelayCommand]
    private void ClearTargetFolder()
    {
        TargetFolder = string.Empty;
    }

    #endregion

    #region Private Methods

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
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
