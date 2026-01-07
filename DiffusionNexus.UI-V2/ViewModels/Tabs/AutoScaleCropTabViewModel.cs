using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Autocropper;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services;

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
/// </summary>
public partial class AutoScaleCropTabViewModel : ObservableObject, IDisposable
{
    private readonly IImageCropperService _cropperService;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;

    #region Observable Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
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

    #region Collections

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

    #region Computed Properties

    public BucketDefinition[] SelectedBuckets => BucketOptions
        .Where(b => b.IsSelected)
        .Select(b => b.Bucket)
        .ToArray();

    public bool HasSelectedBuckets => BucketOptions.Any(b => b.IsSelected);

    public bool IsOverwriteMode => string.IsNullOrWhiteSpace(TargetFolder);

    public bool CanStart =>
        !string.IsNullOrWhiteSpace(SourceFolder) &&
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

    public AutoScaleCropTabViewModel()
    {
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
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            UpdateFolderMetadata();
        }
        else
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

            var result = await _cropperService.ProcessImagesAsync(
                SourceFolder,
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
