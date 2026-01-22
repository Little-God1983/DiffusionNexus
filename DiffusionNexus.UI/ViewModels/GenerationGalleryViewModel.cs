using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Generation Gallery mosaic gallery.
/// </summary>
public partial class GenerationGalleryViewModel : BusyViewModelBase
{
    private readonly IAppSettingsService? _settingsService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly IDatasetState? _datasetState;
    private readonly List<GenerationGalleryMediaItemViewModel> _allMediaItems = [];
    private GenerationGalleryMediaItemViewModel? _lastClickedItem;
    private int _selectionCount;

    public GenerationGalleryViewModel()
    {
        _settingsService = null;
        _eventAggregator = null;
        _datasetState = null;
        LoadDesignData();
    }

    public GenerationGalleryViewModel(
        IAppSettingsService settingsService,
        IDatasetEventAggregator eventAggregator,
        IDatasetState datasetState)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _datasetState = datasetState ?? throw new ArgumentNullException(nameof(datasetState));
        
        _eventAggregator.SettingsSaved += OnSettingsSaved;
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        // Reload gallery when settings change (new folders might be added)
        LoadMediaCommand.Execute(null);
    }

    public ObservableCollection<GenerationGalleryMediaItemViewModel> MediaItems { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } = ["Name", "Creation date"];

    public string ImageExtensionsDisplay => SupportedMediaTypes.ImageExtensionsDisplay;

    public string VideoExtensionsDisplay => SupportedMediaTypes.VideoExtensionsDisplay;

    [ObservableProperty]
    private string _selectedSortOption = "Creation date";

    [ObservableProperty]
    private double _tileWidth = 220;

    [ObservableProperty]
    private string? _noMediaMessage;

    public int SelectionCount
    {
        get => _selectionCount;
        private set => SetProperty(ref _selectionCount, value);
    }

    public bool HasSelection => SelectionCount > 0;

    public string SelectionText => SelectionCount == 1 ? "1 selected" : $"{SelectionCount} selected";

    public bool HasMedia => MediaItems.Count > 0;

    public bool HasNoMedia => !HasMedia;

    [RelayCommand]
    private async Task LoadMediaAsync()
    {
        if (_settingsService is null)
        {
            LoadDesignData();
            return;
        }

        await RunBusyAsync(async () =>
        {
            var settings = await _settingsService.GetSettingsAsync();
            var enabledPaths = GetEnabledGalleryPaths(settings);

            var mediaItems = await Task.Run(() => CollectMediaItems(enabledPaths));
            await ApplyMediaItemsAsync(mediaItems, enabledPaths.Count);
        }, "Loading gallery...");
    }

    partial void OnSelectedSortOptionChanged(string value)
    {
        ApplySorting();
    }

    public void SelectWithModifiers(GenerationGalleryMediaItemViewModel? item, bool isShiftPressed, bool isCtrlPressed)
    {
        if (item is null) return;

        if (isShiftPressed && _lastClickedItem is not null)
        {
            SelectRange(_lastClickedItem, item);
        }
        else if (isCtrlPressed)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            ClearSelectionSilent();
            item.IsSelected = true;
        }

        _lastClickedItem = item;
        UpdateSelectionState();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in MediaItems)
        {
            item.IsSelected = true;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        ClearSelectionSilent();
        UpdateSelectionState();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (DialogService is null) return;

        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Selected Media",
            $"Delete {selectedItems.Count} selected items?");

        if (!confirm) return;

        foreach (var item in selectedItems)
        {
            DeleteFileIfExists(item.FilePath);
            RemoveMediaItem(item);
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private async Task AddSelectedToDatasetAsync()
    {
        if (DialogService is null || _settingsService is null || _eventAggregator is null || _datasetState is null) return;

        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0) return;

        var dialogResult = await DialogService.ShowAddToDatasetDialogAsync(
            selectedItems.Count,
            _datasetState.Datasets,
            _datasetState.AvailableCategories);

        if (!dialogResult.Confirmed) return;

        var selectedFiles = selectedItems.Select(item => item.FilePath).ToList();
        var transferMode = dialogResult.TransferMode;

        await RunBusyAsync(async () =>
        {
            var targetDataset = await ResolveTargetDatasetAsync(dialogResult);
            if (targetDataset is null)
            {
                return;
            }

            var targetVersion = await ResolveTargetVersionAsync(targetDataset, dialogResult);
            if (!targetVersion.HasValue)
            {
                return;
            }

            var destinationFolder = targetDataset.IsVersionedStructure
                ? targetDataset.GetVersionFolderPath(targetVersion.Value)
                : targetDataset.CurrentVersionFolderPath;

            Directory.CreateDirectory(destinationFolder);

            DatasetImportSummary importSummary;
            try
            {
                importSummary = await DatasetImportHelper.ImportFilesAsync(
                    DialogService,
                    destinationFolder,
                    selectedFiles,
                    transferMode == FileTransferMode.Move);
            }
            catch (Exception ex)
            {
                await DialogService.ShowMessageAsync("Add to Dataset", $"Failed to add files: {ex.Message}");
                return;
            }

            if (importSummary.Cancelled)
            {
                return;
            }

            if (transferMode == FileTransferMode.Move && importSummary.ProcessedSourceFiles.Count > 0)
            {
                RemoveMovedItems(selectedItems, importSummary.ProcessedSourceFiles);
            }

            targetDataset.RefreshImageInfo();
            targetDataset.SaveMetadata();

            _eventAggregator.PublishImageAdded(new ImageAddedEventArgs
            {
                Dataset = targetDataset,
                AddedImages = []
            });

            ClearSelectionSilent();
            UpdateSelectionState();
        }, "Adding to dataset...");
    }

    [RelayCommand]
    private async Task DeleteImageAsync(GenerationGalleryMediaItemViewModel? item)
    {
        if (item is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Media",
            $"Delete '{item.FullFileName}'?");

        if (!confirm) return;

        DeleteFileIfExists(item.FilePath);
        RemoveMediaItem(item);
        UpdateSelectionState();
    }

    private static List<string> GetEnabledGalleryPaths(AppSettings settings)
    {
        return settings.ImageGalleries
            .Where(g => g.IsEnabled && !string.IsNullOrWhiteSpace(g.FolderPath))
            .OrderBy(g => g.Order)
            .Select(g => g.FolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<GenerationGalleryMediaItemViewModel> CollectMediaItems(IEnumerable<string> paths)
    {
        var items = new List<GenerationGalleryMediaItemViewModel>();
        foreach (var root in paths)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in EnumerateMediaFiles(root))
            {
                var isVideo = SupportedMediaTypes.IsVideoFile(file);
                var createdAt = File.GetCreationTimeUtc(file);
                items.Add(new GenerationGalleryMediaItemViewModel(file, isVideo, createdAt));
            }
        }

        return items;
    }

    private static IEnumerable<string> EnumerateMediaFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files = [];
            IEnumerable<string> directories = [];

            try
            {
                files = Directory.EnumerateFiles(current, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (SupportedMediaTypes.IsMediaFile(file))
                {
                    yield return file;
                }
            }

            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                pending.Push(directory);
            }
        }
    }

    private async Task ApplyMediaItemsAsync(List<GenerationGalleryMediaItemViewModel> items, int enabledSourceCount)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyMediaItems(items, enabledSourceCount);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => ApplyMediaItems(items, enabledSourceCount));
    }

    private void ApplyMediaItems(List<GenerationGalleryMediaItemViewModel> items, int enabledSourceCount)
    {
        _allMediaItems.Clear();
        _allMediaItems.AddRange(items);

        ApplySorting();

        NoMediaMessage = enabledSourceCount == 0
            ? "No generation gallery folders are enabled. Configure Generation Galleries in Settings to get started."
            : "No media found in enabled generation gallery folders. Check your Generation Galleries in Settings.";

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        _lastClickedItem = null;
        UpdateSelectionState();
    }

    private void ApplySorting()
    {
        IEnumerable<GenerationGalleryMediaItemViewModel> sorted = _allMediaItems;

        if (string.Equals(SelectedSortOption, "Creation date", StringComparison.OrdinalIgnoreCase))
        {
            sorted = sorted.OrderByDescending(item => item.CreatedAtUtc)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            sorted = sorted.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        }

        MediaItems.Clear();
        foreach (var item in sorted)
        {
            MediaItems.Add(item);
        }

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        UpdateSelectionState();
    }

    private void LoadDesignData()
    {
        _allMediaItems.Clear();
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-01.png", false, DateTime.UtcNow.AddDays(-1)));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-02.jpg", false, DateTime.UtcNow));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Videos\\Sample-03.mp4", true, DateTime.UtcNow.AddHours(-4)));

        ApplySorting();
    }

    private void SelectRange(GenerationGalleryMediaItemViewModel from, GenerationGalleryMediaItemViewModel to)
    {
        var fromIndex = MediaItems.IndexOf(from);
        var toIndex = MediaItems.IndexOf(to);

        if (fromIndex == -1 || toIndex == -1) return;

        var startIndex = Math.Min(fromIndex, toIndex);
        var endIndex = Math.Max(fromIndex, toIndex);

        for (var i = startIndex; i <= endIndex; i++)
        {
            MediaItems[i].IsSelected = true;
        }
    }

    private void ClearSelectionSilent()
    {
        foreach (var item in MediaItems)
        {
            item.IsSelected = false;
        }
    }

    private void UpdateSelectionState()
    {
        SelectionCount = MediaItems.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
    }

    private void RemoveMediaItem(GenerationGalleryMediaItemViewModel item)
    {
        _allMediaItems.Remove(item);
        MediaItems.Remove(item);
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
    }

    private static void DeleteFileIfExists(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private async Task<DatasetCardViewModel?> ResolveTargetDatasetAsync(AddToDatasetResult dialogResult)
    {
        if (dialogResult.TargetMode == DatasetTargetMode.Existing)
        {
            return dialogResult.SelectedDataset;
        }

        if (_settingsService is null || _eventAggregator is null || _datasetState is null || dialogResult.NewDataset is null)
        {
            return null;
        }

        var creationOutcome = await DatasetCreationHelper.TryCreateDatasetAsync(_settingsService, dialogResult.NewDataset);
        _datasetState.SetStorageConfigured(creationOutcome.StorageConfigured);

        if (!creationOutcome.Success || creationOutcome.Dataset is null)
        {
            if (DialogService is not null && !string.IsNullOrWhiteSpace(creationOutcome.ErrorMessage))
            {
                await DialogService.ShowMessageAsync(\"Create Dataset\", creationOutcome.ErrorMessage);
            }
            return null;
        }

        _datasetState.Datasets.Add(creationOutcome.Dataset);
        _eventAggregator.PublishDatasetCreated(new DatasetCreatedEventArgs
        {
            Dataset = creationOutcome.Dataset
        });

        return creationOutcome.Dataset;
    }

    private async Task<int?> ResolveTargetVersionAsync(DatasetCardViewModel dataset, AddToDatasetResult dialogResult)
    {
        if (dialogResult.VersionMode == DatasetVersionMode.ExistingVersion)
        {
            var version = dialogResult.TargetVersion ?? dataset.CurrentVersion;
            dataset.CurrentVersion = version;
            dataset.SaveMetadata();
            return version;
        }

        var parentVersion = dataset.CurrentVersion;
        if (!dataset.IsVersionedStructure && dataset.TotalMediaCount > 0)
        {
            await DatasetVersionHelper.MigrateLegacyToVersionedAsync(dataset);
        }

        var nextVersion = dataset.GetNextVersionNumber();
        var destPath = dataset.GetVersionFolderPath(nextVersion);
        Directory.CreateDirectory(destPath);

        var parentNsfw = dataset.VersionNsfwFlags.GetValueOrDefault(parentVersion, dataset.IsNsfw);
        dataset.VersionNsfwFlags[nextVersion] = parentNsfw;
        dataset.RecordBranch(nextVersion, parentVersion);
        dataset.CurrentVersion = nextVersion;
        dataset.IsVersionedStructure = true;
        dataset.TotalVersions = Math.Max(dataset.TotalVersions, dataset.GetAllVersionNumbers().Count);
        dataset.SaveMetadata();

        _eventAggregator!.PublishVersionCreated(new VersionCreatedEventArgs
        {
            Dataset = dataset,
            NewVersion = nextVersion,
            BranchedFromVersion = parentVersion
        });

        return nextVersion;
    }

    private void RemoveMovedItems(
        IReadOnlyList<GenerationGalleryMediaItemViewModel> selectedItems,
        IReadOnlyList<string> processedSources)
    {
        var processedSet = processedSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selectedItems.Where(item => processedSet.Contains(item.FilePath)))
        {
            RemoveMediaItem(item);
        }
    }
}
