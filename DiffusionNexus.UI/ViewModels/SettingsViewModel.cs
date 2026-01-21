using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the application settings view.
/// </summary>
public partial class SettingsViewModel : BusyViewModelBase
{
    private readonly IAppSettingsService _settingsService;
    private readonly ISecureStorage _secureStorage;
    private readonly IDatasetBackupService? _backupService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly IActivityLogService? _activityLogService;

    #region Observable Properties

    /// <summary>
    /// The Civitai API key (decrypted, in memory only).
    /// </summary>
    [ObservableProperty]
    private string? _civitaiApiKey;

    /// <summary>
    /// The Huggingface API key (decrypted, in memory only).
    /// </summary>
    [ObservableProperty]
    private string? _huggingfaceApiKey;

    /// <summary>
    /// Whether to show NSFW content by default.
    /// </summary>
    [ObservableProperty]
    private bool _showNsfw;

    /// <summary>
    /// Whether to generate thumbnails from video files.
    /// </summary>
    [ObservableProperty]
    private bool _generateVideoThumbnails = true;

    /// <summary>
    /// Whether to show video preview (experimental).
    /// </summary>
    [ObservableProperty]
    private bool _showVideoPreview;

    /// <summary>
    /// Whether to use A1111/Forge style prompts.
    /// </summary>
    [ObservableProperty]
    private bool _useForgeStylePrompts = true;

    /// <summary>
    /// Whether to merge LoRA sources by base model.
    /// </summary>
    [ObservableProperty]
    private bool _mergeLoraSources;

    /// <summary>
    /// Default source folder for LoRA Sort.
    /// </summary>
    [ObservableProperty]
    private string? _loraSortSourcePath;

    /// <summary>
    /// Default target folder for LoRA Sort.
    /// </summary>
    [ObservableProperty]
    private string? _loraSortTargetPath;

    /// <summary>
    /// Default storage path for LoRA training datasets.
    /// </summary>
    [ObservableProperty]
    private string? _datasetStoragePath;

    /// <summary>
    /// Whether automatic backup is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _autoBackupEnabled;

    /// <summary>
    /// Days component of the backup interval (1-30).
    /// </summary>
    [ObservableProperty]
    private int _autoBackupIntervalDays = 1;

    /// <summary>
    /// Hours component of the backup interval (0-23).
    /// </summary>
    [ObservableProperty]
    private int _autoBackupIntervalHours;

    /// <summary>
    /// Folder path for automatic backups.
    /// </summary>
    [ObservableProperty]
    private string? _autoBackupLocation;

    /// <summary>
    /// Maximum number of backups to keep (oldest are deleted).
    /// </summary>
    [ObservableProperty]
    private int _maxBackups = 10;

    /// <summary>
    /// Validation error message for backup location.
    /// </summary>
    [ObservableProperty]
    private string? _autoBackupLocationError;

    /// <summary>
    /// Validation error message for backup interval.
    /// </summary>
    [ObservableProperty]
    private string? _autoBackupIntervalError;

    /// <summary>
    /// Whether a backup or restore operation is currently in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isBackupInProgress;

    /// <summary>
    /// Available days for backup interval (0-30).
    /// </summary>
    public IReadOnlyList<int> AvailableBackupDays { get; } = Enumerable.Range(0, 31).ToList();

    /// <summary>
    /// Available hours for backup interval (0-23).
    /// </summary>
    public IReadOnlyList<int> AvailableBackupHours { get; } = Enumerable.Range(0, 24).ToList();

    /// <summary>
    /// Available options for maximum backups (1-50).
    /// </summary>
    public IReadOnlyList<int> AvailableMaxBackups { get; } = Enumerable.Range(1, 50).ToList();

    /// <summary>
    /// Collection of LoRA source folders.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<LoraSourceViewModel> _loraSources = [];

    /// <summary>
    /// Collection of LoRA source folders.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ImageGalleryViewModel> _imageGallerySources = [];

    /// <summary>
    /// Collection of dataset categories.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<DatasetCategoryViewModel> _datasetCategories = [];

    /// <summary>
    /// Status message for the user.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Whether settings have been modified.
    /// </summary>
    [ObservableProperty]
    private bool _hasChanges;

    #endregion

    /// <summary>
    /// Creates a new SettingsViewModel.
    /// </summary>
    public SettingsViewModel(
        IAppSettingsService settingsService, 
        ISecureStorage secureStorage,
        IDatasetBackupService? backupService = null,
        IDatasetEventAggregator? eventAggregator = null,
        IActivityLogService? activityLogService = null)
    {
        _settingsService = settingsService;
        _secureStorage = secureStorage;
        _backupService = backupService;
        _eventAggregator = eventAggregator;
        _activityLogService = activityLogService;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public SettingsViewModel()
    {
        _settingsService = null!;
        _secureStorage = null!;
        _backupService = null;
        _eventAggregator = null;
        _activityLogService = null;

        // Design-time data
        LoraSources =
        [
            new LoraSourceViewModel { FolderPath = @"C:\Models\LoRA", IsEnabled = true },
            new LoraSourceViewModel { FolderPath = @"D:\AI\Models\LoRA", IsEnabled = true },
        ];

        DatasetCategories =
        [
            new DatasetCategoryViewModel { Name = "Category 1", Description = "First category", IsDefault = true },
            new DatasetCategoryViewModel { Name = "Category 2", Description = "Second category", IsDefault = false },
        ];
    }

    /// <summary>
    /// Loads settings from the database.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            var settings = await _settingsService.GetSettingsAsync();

            // Decrypt API key
            CivitaiApiKey = _secureStorage.Decrypt(settings.EncryptedCivitaiApiKey);

            // Map settings to view model
            ShowNsfw = settings.ShowNsfw;
            GenerateVideoThumbnails = settings.GenerateVideoThumbnails;
            ShowVideoPreview = settings.ShowVideoPreview;
            UseForgeStylePrompts = settings.UseForgeStylePrompts;
            MergeLoraSources = settings.MergeLoraSources;
            LoraSortSourcePath = settings.LoraSortSourcePath;
            LoraSortTargetPath = settings.LoraSortTargetPath;
            DatasetStoragePath = settings.DatasetStoragePath;
            AutoBackupEnabled = settings.AutoBackupEnabled;
            AutoBackupIntervalDays = settings.AutoBackupIntervalDays;
            AutoBackupIntervalHours = settings.AutoBackupIntervalHours;
            AutoBackupLocation = settings.AutoBackupLocation;
            MaxBackups = settings.MaxBackups;

            // Map LoRA sources
            foreach (var existing in LoraSources)
            {
                existing.SourceChanged -= OnLoraSourceChanged;
            }
            LoraSources.Clear();
            
            foreach (var source in settings.LoraSources.OrderBy(s => s.Order))
            {
                var sourceVm = new LoraSourceViewModel
                {
                    Id = source.Id,
                    FolderPath = source.FolderPath,
                    IsEnabled = source.IsEnabled
                };
                sourceVm.SourceChanged += OnLoraSourceChanged;
                LoraSources.Add(sourceVm);
            }

            // Map dataset categories
            foreach (var existing in DatasetCategories)
            {
                existing.CategoryChanged -= OnCategoryChanged;
            }
            DatasetCategories.Clear();

            foreach (var category in settings.DatasetCategories.OrderBy(c => c.Order))
            {
                var categoryVm = new DatasetCategoryViewModel
                {
                    Id = category.Id,
                    Name = category.Name,
                    Description = category.Description,
                    IsDefault = category.IsDefault
                };
                categoryVm.CategoryChanged += OnCategoryChanged;
                DatasetCategories.Add(categoryVm);
            }

            HasChanges = false;
            StatusMessage = null;
        }, "Loading settings...");
    }

    /// <summary>
    /// Saves settings to the database.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        // Validate backup settings before saving
        if (!ValidateBackupSettings())
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var settings = await _settingsService.GetSettingsAsync();

            // Encrypt and set API key
            settings.EncryptedCivitaiApiKey = string.IsNullOrWhiteSpace(CivitaiApiKey)
                ? null
                : _secureStorage.Encrypt(CivitaiApiKey);

            // Map view model to settings
            settings.ShowNsfw = ShowNsfw;
            settings.GenerateVideoThumbnails = GenerateVideoThumbnails;
            settings.ShowVideoPreview = ShowVideoPreview;
            settings.UseForgeStylePrompts = UseForgeStylePrompts;
            settings.MergeLoraSources = MergeLoraSources;
            settings.LoraSortSourcePath = LoraSortSourcePath;
            settings.LoraSortTargetPath = LoraSortTargetPath;
            settings.DatasetStoragePath = DatasetStoragePath;
            settings.AutoBackupEnabled = AutoBackupEnabled;
            settings.AutoBackupIntervalDays = AutoBackupIntervalDays;
            settings.AutoBackupIntervalHours = AutoBackupIntervalHours;
            settings.AutoBackupLocation = AutoBackupLocation;
            settings.MaxBackups = MaxBackups;

            // Map LoRA sources (remove empty ones)
            settings.LoraSources.Clear();
            var order = 0;
            foreach (var sourceVm in LoraSources.Where(s => !string.IsNullOrWhiteSpace(s.FolderPath)))
            {
                settings.LoraSources.Add(new LoraSource
                {
                    Id = sourceVm.Id,
                    AppSettingsId = 1,
                    FolderPath = sourceVm.FolderPath!,
                    IsEnabled = sourceVm.IsEnabled,
                    Order = order++
                });
            }

            // Map dataset categories (remove empty ones)
            settings.DatasetCategories.Clear();
            var categoryOrder = 0;
            foreach (var categoryVm in DatasetCategories.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            {
                settings.DatasetCategories.Add(new DatasetCategory
                {
                    Id = categoryVm.Id,
                    AppSettingsId = 1,
                    Name = categoryVm.Name!,
                    Description = categoryVm.Description,
                    IsDefault = categoryVm.IsDefault,
                    Order = categoryOrder++
                });
            }

            await _settingsService.SaveSettingsAsync(settings);

            HasChanges = false;
            StatusMessage = "Settings saved successfully.";

            // Notify other components that settings have changed
            _eventAggregator?.PublishSettingsSaved(new SettingsSavedEventArgs());
        }, "Saving settings...");
    }

    /// <summary>
    /// Deletes the API key.
    /// </summary>
    [RelayCommand]
    private void DeleteApiKey()
    {
        CivitaiApiKey = null;
        HasChanges = true;
    }

    /// <summary>
    /// Deletes the Huggingface API key.
    /// </summary>
    [RelayCommand]
    private void DeleteHuggingfaceApiKey()
    {
        HuggingfaceApiKey = null;
        HasChanges = true;
    }

    /// <summary>
    /// Adds a new LoRA source folder.
    /// </summary>
    [RelayCommand]
    private void AddLoraSource()
    {
        var source = new LoraSourceViewModel { IsEnabled = true };
        source.SourceChanged += OnLoraSourceChanged;
        LoraSources.Add(source);
        HasChanges = true;
    }

    /// <summary>
    /// Removes a LoRA source folder.
    /// </summary>
    [RelayCommand]
    private void RemoveLoraSource(LoraSourceViewModel? source)
    {
        if (source is not null)
        {
            source.SourceChanged -= OnLoraSourceChanged;
            LoraSources.Remove(source);
            HasChanges = true;
        }
    }

    /// <summary>
    /// Browse for a LoRA source folder.
    /// </summary>
    [RelayCommand]
    private async Task BrowseLoraSourceAsync(LoraSourceViewModel? source)
    {
        if (source is null || DialogService is null)
        {
            return;
        }

        var path = await DialogService.ShowOpenFolderDialogAsync("Select LoRA Folder");
        if (!string.IsNullOrEmpty(path))
        {
            source.FolderPath = path;
            HasChanges = true;
        }
    }

    /// <summary>
    /// Browse for LoRA Sort source folder.
    /// </summary>
    [RelayCommand]
    private async Task BrowseLoraSortSourceAsync()
    {
        if (DialogService is null)
        {
            return;
        }

        var path = await DialogService.ShowOpenFolderDialogAsync("Select Source Folder");
        if (!string.IsNullOrEmpty(path))
        {
            LoraSortSourcePath = path;
            HasChanges = true;
        }
    }

    /// <summary>
    /// Browse for LoRA Sort target folder.
    /// </summary>
    [RelayCommand]
    private async Task BrowseLoraSortTargetAsync()
    {
        if (DialogService is null)
        {
            return;
        }

        var path = await DialogService.ShowOpenFolderDialogAsync("Select Target Folder");
        if (!string.IsNullOrEmpty(path))
        {
            LoraSortTargetPath = path;
            HasChanges = true;
        }
    }


    /// <summary>
    /// Adds a new LoRA source folder.
    /// </summary>
    [RelayCommand]
    private void AddImageGallerySource()
    {
        var source = new ImageGalleryViewModel { IsEnabled = true };
        source.SourceChanged += OnImageGalleryChanged;
        ImageGallerySources.Add(source);
        HasChanges = true;
    }

    /// <summary>
    /// Removes a LoRA source folder.
    /// </summary>
    [RelayCommand]
    private void RemoveImageGallerySource(ImageGalleryViewModel? source)
    {
        if (source is not null)
        {
            source.SourceChanged -= OnImageGalleryChanged;
            ImageGallerySources.Remove(source);
            HasChanges = true;
        }
    }



    /// <summary>
    /// Browse for Dataset Storage folder.
    /// </summary>
    [RelayCommand]
    private async Task BrowseDatasetStorageAsync()
    {
        if (DialogService is null)
        {
            return;
        }

        var path = await DialogService.ShowOpenFolderDialogAsync("Select Dataset Storage Folder");
        if (!string.IsNullOrEmpty(path))
        {
            DatasetStoragePath = path;
            ValidateAutoBackupLocation();
            HasChanges = true;
        }
    }

    /// <summary>
    /// Browse for Auto Backup location folder.
    /// </summary>
    [RelayCommand]
    private async Task BrowseAutoBackupLocationAsync()
    {
        if (DialogService is null)
        {
            return;
        }

        var path = await DialogService.ShowOpenFolderDialogAsync("Select Backup Location Folder");
        if (!string.IsNullOrEmpty(path))
        {
            AutoBackupLocation = path;
            ValidateAutoBackupLocation();
            HasChanges = true;
        }
    }

    /// <summary>
    /// Validates that the backup location is not the same as or a subfolder of the dataset storage path.
    /// </summary>
    private void ValidateAutoBackupLocation()
    {
        AutoBackupLocationError = null;

        if (string.IsNullOrWhiteSpace(AutoBackupLocation) || string.IsNullOrWhiteSpace(DatasetStoragePath))
        {
            return;
        }

        var backupPath = Path.GetFullPath(AutoBackupLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var storagePath = Path.GetFullPath(DatasetStoragePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(backupPath, storagePath, StringComparison.OrdinalIgnoreCase))
        {
            AutoBackupLocationError = "Backup location cannot be the same as the Dataset Storage folder.";
            return;
        }

        // Check if backup path is a subfolder of storage path
        var storagePathWithSep = storagePath + Path.DirectorySeparatorChar;
        if (backupPath.StartsWith(storagePathWithSep, StringComparison.OrdinalIgnoreCase))
        {
            AutoBackupLocationError = "Backup location cannot be a subfolder of the Dataset Storage folder.";
        }
    }

    /// <summary>
    /// Validates backup settings when auto backup is enabled.
    /// </summary>
    /// <returns>True if valid, false otherwise.</returns>
    private bool ValidateBackupSettings()
    {
        // Clear previous errors
        AutoBackupLocationError = null;
        AutoBackupIntervalError = null;

        if (!AutoBackupEnabled)
        {
            return true;
        }

        var isValid = true;

        // Check backup location is set
        if (string.IsNullOrWhiteSpace(AutoBackupLocation))
        {
            AutoBackupLocationError = "Backup location is required when automatic backup is enabled.";
            isValid = false;
        }
        else
        {
            // Run existing path validation
            ValidateAutoBackupLocation();
            if (!string.IsNullOrEmpty(AutoBackupLocationError))
            {
                isValid = false;
            }
        }

        // Check that either days or hours is > 0
        if (AutoBackupIntervalDays == 0 && AutoBackupIntervalHours == 0)
        {
            AutoBackupIntervalError = "Backup interval must be at least 1 hour or 1 day.";
            isValid = false;
        }

        if (!isValid)
        {
            StatusMessage = "Please fix the validation errors before saving.";
        }

        return isValid;
    }

    /// <summary>
    /// Adds a new dataset category.
    /// </summary>
    [RelayCommand]
    private void AddCategory()
    {
        var category = new DatasetCategoryViewModel { IsDefault = false };
        category.CategoryChanged += OnCategoryChanged;
        DatasetCategories.Add(category);
        HasChanges = true;
    }

    /// <summary>
    /// Removes a dataset category (only non-default ones).
    /// </summary>
    [RelayCommand]
    private void RemoveCategory(DatasetCategoryViewModel? category)
    {
        if (category is null || category.IsDefault)
            return;

        category.CategoryChanged -= OnCategoryChanged;
        DatasetCategories.Remove(category);
        HasChanges = true;
    }

    /// <summary>
    /// Opens the Dataset Storage folder in the file explorer.
    /// </summary>
    [RelayCommand]
    private void OpenDatasetStorageFolder()
    {
        if (string.IsNullOrWhiteSpace(DatasetStoragePath) || !Directory.Exists(DatasetStoragePath))
        {
            StatusMessage = "Dataset storage folder is not configured or does not exist.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DatasetStoragePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the Backup Location folder in the file explorer.
    /// </summary>
    [RelayCommand]
    private void OpenBackupLocationFolder()
    {
        if (string.IsNullOrWhiteSpace(AutoBackupLocation) || !Directory.Exists(AutoBackupLocation))
        {
            StatusMessage = "Backup location is not configured or does not exist.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AutoBackupLocation,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs a backup immediately.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteBackup))]
    private async Task BackupNowAsync()
    {
        if (_backupService is null)
        {
            StatusMessage = "Backup service is not available.";
            return;
        }

        if (_backupService.IsOperationInProgress)
        {
            StatusMessage = "A backup or restore operation is already in progress.";
            return;
        }

        IsBackupInProgress = true;
        BackupNowCommand.NotifyCanExecuteChanged();
        LoadBackupCommand.NotifyCanExecuteChanged();

        // Start backup progress tracking in the status bar
        _activityLogService?.StartBackupProgress("Backing up datasets");

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    BusyMessage = $"Backup: {p.Phase} ({p.ProgressPercent}%)";
                    _activityLogService?.ReportBackupProgress(p.ProgressPercent, p.Phase);
                });
            });

            // Run backup on a background thread to avoid blocking UI
            var result = await Task.Run(async () => await _backupService.BackupDatasetsAsync(progress));

            if (result.Success)
            {
                _activityLogService?.CompleteBackupProgress(true, $"Backup completed: {result.FilesBackedUp} files");
                StatusMessage = $"Backup completed: {result.FilesBackedUp} files backed up.";
            }
            else
            {
                _activityLogService?.CompleteBackupProgress(false, $"Backup failed: {result.ErrorMessage}");
                StatusMessage = $"Backup failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            _activityLogService?.CompleteBackupProgress(false, $"Backup error: {ex.Message}");
            StatusMessage = $"Backup error: {ex.Message}";
        }
        finally
        {
            IsBackupInProgress = false;
            BackupNowCommand.NotifyCanExecuteChanged();
            LoadBackupCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanExecuteBackup()
    {
        return AutoBackupEnabled 
            && !string.IsNullOrWhiteSpace(AutoBackupLocation)
            && !string.IsNullOrWhiteSpace(DatasetStoragePath)
            && !IsBackupInProgress;
    }

    /// <summary>
    /// Opens a file picker to select a backup ZIP and shows the comparison dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteLoadBackup))]
    private async Task LoadBackupAsync()
    {
        if (DialogService is null || _backupService is null)
        {
            StatusMessage = "Dialog service or backup service is not available.";
            return;
        }

        if (_backupService.IsOperationInProgress)
        {
            StatusMessage = "A backup or restore operation is already in progress.";
            return;
        }

        // Determine starting folder - use configured backup location if available
        var startFolder = !string.IsNullOrWhiteSpace(AutoBackupLocation) && Directory.Exists(AutoBackupLocation)
            ? AutoBackupLocation
            : null;

        // Show file picker for ZIP files
        var backupPath = await DialogService.ShowOpenFileDialogAsync(
            "Select Backup to Restore",
            startFolder ?? string.Empty,
            "*.zip");

        if (string.IsNullOrEmpty(backupPath))
        {
            return; // User cancelled
        }

        // If backup location wasn't set, set it to the folder containing the selected file
        if (string.IsNullOrWhiteSpace(AutoBackupLocation))
        {
            var backupFolder = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(backupFolder))
            {
                AutoBackupLocation = backupFolder;
                HasChanges = true;
            }
        }

        IsBackupInProgress = true;
        BackupNowCommand.NotifyCanExecuteChanged();
        LoadBackupCommand.NotifyCanExecuteChanged();

        try
        {
            await RunBusyAsync(async () =>
            {
                // Analyze the backup
                var backupAnalysis = await _backupService.AnalyzeBackupAsync(backupPath);
                if (!backupAnalysis.Success)
                {
                    StatusMessage = $"Failed to analyze backup: {backupAnalysis.ErrorMessage}";
                    return;
                }

                // Get current storage stats
                var currentStats = await _backupService.GetCurrentStorageStatsAsync();

                // Prepare comparison data
                var currentData = new Services.BackupCompareData
                {
                    Label = "Current",
                    Date = currentStats.CurrentDate,
                    DatasetCount = currentStats.DatasetCount,
                    ImageCount = currentStats.ImageCount,
                    VideoCount = currentStats.VideoCount,
                    CaptionCount = currentStats.CaptionCount,
                    TotalSizeBytes = currentStats.TotalSizeBytes
                };

                var backupData = new Services.BackupCompareData
                {
                    Label = "Backup",
                    Date = backupAnalysis.BackupDate ?? DateTimeOffset.MinValue,
                    DatasetCount = backupAnalysis.DatasetCount,
                    ImageCount = backupAnalysis.ImageCount,
                    VideoCount = backupAnalysis.VideoCount,
                    CaptionCount = backupAnalysis.CaptionCount,
                    TotalSizeBytes = backupAnalysis.TotalSizeBytes
                };

                // Show comparison dialog
                var shouldRestore = await DialogService.ShowBackupCompareDialogAsync(currentData, backupData);

                if (!shouldRestore)
                {
                    StatusMessage = "Restore cancelled.";
                    return;
                }

                // Perform the restore
                BusyMessage = "Restoring backup...";

                var progress = new Progress<BackupProgress>(p =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        BusyMessage = $"Restore: {p.Phase} ({p.ProgressPercent}%)";
                    });
                });

                var result = await _backupService.RestoreBackupAsync(backupPath, progress);

                if (result.Success)
                {
                    StatusMessage = $"Restore completed: {result.FilesRestored} files restored.";
                }
                else
                {
                    StatusMessage = $"Restore failed: {result.ErrorMessage}";
                }
            }, "Analyzing backup...");
        }
        finally
        {
            IsBackupInProgress = false;
            BackupNowCommand.NotifyCanExecuteChanged();
            LoadBackupCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanExecuteLoadBackup()
    {
        return !string.IsNullOrWhiteSpace(DatasetStoragePath) && !IsBackupInProgress;
    }

    private void OnLoraSourceChanged(object? sender, EventArgs e)
    {
        HasChanges = true;
    }

    private void OnCategoryChanged(object? sender, EventArgs e)
    {
        HasChanges = true;
    }

    private void OnImageGalleryChanged(object? sender, EventArgs e)
    {
        HasChanges = true;
    }

    partial void OnCivitaiApiKeyChanged(string? value) => HasChanges = true;
    partial void OnHuggingfaceApiKeyChanged(string? value) => HasChanges = true;
    partial void OnShowNsfwChanged(bool value) => HasChanges = true;
    partial void OnGenerateVideoThumbnailsChanged(bool value) => HasChanges = true;
    partial void OnShowVideoPreviewChanged(bool value) => HasChanges = true;
    partial void OnUseForgeStylePromptsChanged(bool value) => HasChanges = true;
    partial void OnMergeLoraSourcesChanged(bool value) => HasChanges = true;
    partial void OnLoraSortSourcePathChanged(string? value) => HasChanges = true;
    partial void OnLoraSortTargetPathChanged(string? value) => HasChanges = true;
    partial void OnDatasetStoragePathChanged(string? value)
    {
        HasChanges = true;
        ValidateAutoBackupLocation();
    }
    partial void OnAutoBackupEnabledChanged(bool value)
    {
        HasChanges = true;
        // Clear errors when disabling auto backup
        if (!value)
        {
            AutoBackupIntervalError = null;
            AutoBackupLocationError = null;
        }
    }

    partial void OnAutoBackupIntervalDaysChanged(int value)
    {
        HasChanges = true;
        // Clear interval error when user changes value
        AutoBackupIntervalError = null;
    }

    partial void OnAutoBackupIntervalHoursChanged(int value)
    {
        HasChanges = true;
        // Clear interval error when user changes value
        AutoBackupIntervalError = null;
    }

    partial void OnAutoBackupLocationChanged(string? value)
    {
        HasChanges = true;
        ValidateAutoBackupLocation();
    }
    partial void OnMaxBackupsChanged(int value) => HasChanges = true;
}

/// <summary>
/// ViewModel for a single dataset category.
/// </summary>
public partial class DatasetCategoryViewModel : ObservableObject
{
    /// <summary>
    /// Database ID (0 for new categories).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Display order (stable across database recreations).
    /// Default categories: Character=0, Style=1, Concept=2.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Category name.
    /// </summary>
    [ObservableProperty]
    private string? _name;

    /// <summary>
    /// Category description.
    /// </summary>
    [ObservableProperty]
    private string? _description;

    /// <summary>
    /// Whether this is a default category (cannot be deleted).
    /// </summary>
    [ObservableProperty]
    private bool _isDefault;

    /// <summary>
    /// Event raised when any property changes.
    /// </summary>
    public event EventHandler? CategoryChanged;

    partial void OnNameChanged(string? value) => CategoryChanged?.Invoke(this, EventArgs.Empty);
    partial void OnDescriptionChanged(string? value) => CategoryChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// ViewModel for a single LoRA source folder.
/// </summary>
public partial class LoraSourceViewModel : ObservableObject
{
    /// <summary>
    /// Database ID (0 for new sources).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Folder path.
    /// </summary>
    [ObservableProperty]
    private string? _folderPath;

    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// Event raised when any property changes (for parent to detect changes).
    /// </summary>
    public event EventHandler? SourceChanged;

    partial void OnFolderPathChanged(string? value) => SourceChanged?.Invoke(this, EventArgs.Empty);
    partial void OnIsEnabledChanged(bool value) => SourceChanged?.Invoke(this, EventArgs.Empty);
}
public partial class ImageGalleryViewModel : ObservableObject
{
    /// <summary>
    /// Database ID (0 for new sources).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Folder path.
    /// </summary>
    [ObservableProperty]
    private string? _folderPath;

    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// Event raised when any property changes (for parent to detect changes).
    /// </summary>
    public event EventHandler? SourceChanged;

    partial void OnFolderPathChanged(string? value) => SourceChanged?.Invoke(this, EventArgs.Empty);
    partial void OnIsEnabledChanged(bool value) => SourceChanged?.Invoke(this, EventArgs.Empty);
}
