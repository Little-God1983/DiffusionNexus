using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the application settings view.
/// </summary>
public partial class SettingsViewModel : BusyViewModelBase
{
    private readonly IAppSettingsService _settingsService;
    private readonly ISecureStorage _secureStorage;

    #region Observable Properties

    /// <summary>
    /// The Civitai API key (decrypted, in memory only).
    /// </summary>
    [ObservableProperty]
    private string? _civitaiApiKey;

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
    /// Validation error message for backup location.
    /// </summary>
    [ObservableProperty]
    private string? _autoBackupLocationError;

    /// <summary>
    /// Available days for backup interval (1-30).
    /// </summary>
    public IReadOnlyList<int> AvailableBackupDays { get; } = Enumerable.Range(1, 30).ToList();

    /// <summary>
    /// Available hours for backup interval (0-23).
    /// </summary>
    public IReadOnlyList<int> AvailableBackupHours { get; } = Enumerable.Range(0, 24).ToList();

    /// <summary>
    /// Collection of LoRA source folders.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<LoraSourceViewModel> _loraSources = [];

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
    public SettingsViewModel(IAppSettingsService settingsService, ISecureStorage secureStorage)
    {
        _settingsService = settingsService;
        _secureStorage = secureStorage;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public SettingsViewModel()
    {
        _settingsService = null!;
        _secureStorage = null!;

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

            // Map LoRA sources (remove empty ones)
            settings.LoraSources.Clear();
            var order = 0;
            foreach (var sourceVm in LoraSources.Where(s => !string.IsNullOrWhiteSpace(s.FolderPath)))
            {
                settings.LoraSources.Add(new LoraSource
                {
                    Id = sourceVm.Id,
                    AppSettingsId = 1, // Always link to the singleton settings
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

    private void OnLoraSourceChanged(object? sender, EventArgs e)
    {
        HasChanges = true;
    }

    private void OnCategoryChanged(object? sender, EventArgs e)
    {
        HasChanges = true;
    }

    partial void OnCivitaiApiKeyChanged(string? value) => HasChanges = true;
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
    partial void OnAutoBackupEnabledChanged(bool value) => HasChanges = true;
    partial void OnAutoBackupIntervalDaysChanged(int value) => HasChanges = true;
    partial void OnAutoBackupIntervalHoursChanged(int value) => HasChanges = true;
    partial void OnAutoBackupLocationChanged(string? value)
    {
        HasChanges = true;
        ValidateAutoBackupLocation();
    }
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
