using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
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
    private readonly IVideoThumbnailService? _videoThumbnailService;
    private readonly List<GenerationGalleryMediaItemViewModel> _allMediaItems = [];
    private GenerationGalleryMediaItemViewModel? _lastClickedItem;
    private int _selectionCount;
    private readonly string[] _dateGroupOptions = ["Day", "Week", "Month", "Year", "None"];
    private readonly string[] _nameGroupOptions = ["Folder", "None"];

    public GenerationGalleryViewModel()
    {
        _settingsService = null;
        _eventAggregator = null;
        _datasetState = null;
        _videoThumbnailService = null;
        LoadDesignData();
    }

    public GenerationGalleryViewModel(
        IAppSettingsService settingsService,
        IDatasetEventAggregator eventAggregator,
        IDatasetState datasetState,
        IVideoThumbnailService? videoThumbnailService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _datasetState = datasetState ?? throw new ArgumentNullException(nameof(datasetState));
        _videoThumbnailService = videoThumbnailService;
        
        _eventAggregator.SettingsSaved += OnSettingsSaved;
        UpdateGroupOptionsForSort();
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        // Reload gallery when settings change (new folders might be added)
        LoadMediaCommand.Execute(null);
    }

    public ObservableCollection<GenerationGalleryMediaItemViewModel> MediaItems { get; } = [];

    public ObservableCollection<GenerationGalleryGroupViewModel> GroupedMediaItems { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } = ["Name", "Creation date"];

    public ObservableCollection<string> GroupOptions { get; } = [];

    public string ImageExtensionsDisplay => SupportedMediaTypes.ImageExtensionsDisplay;

    public string VideoExtensionsDisplay => SupportedMediaTypes.VideoExtensionsDisplay;

    [ObservableProperty]
    private string _selectedSortOption = "Creation date";

    [ObservableProperty]
    private string _selectedGroupOption = "None";

    [ObservableProperty]
    private bool _includeSubfolders = true;

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

    public bool IsNameSortSelected => string.Equals(SelectedSortOption, "Name", StringComparison.OrdinalIgnoreCase);

    public bool IsFolderGroupingSelected => IsNameSortSelected
        && string.Equals(SelectedGroupOption, "Folder", StringComparison.OrdinalIgnoreCase);

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
        UpdateGroupOptionsForSort();
        ApplySortingAndGrouping();
        OnPropertyChanged(nameof(IsNameSortSelected));
        OnPropertyChanged(nameof(IsFolderGroupingSelected));
    }

    partial void OnSelectedGroupOptionChanged(string value)
    {
        ApplyGrouping();
        OnPropertyChanged(nameof(IsFolderGroupingSelected));
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        if (IsFolderGroupingSelected)
        {
            ApplyGrouping();
        }
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

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddSelectedToDatasetAsync()
    {
        if (DialogService is null || _settingsService is null || _datasetState is null)
        {
            return;
        }

        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0) return;

        var dialogResult = await DialogService.ShowAddToDatasetDialogAsync(
            selectedItems.Count,
            _datasetState.Datasets);

        if (!dialogResult.Confirmed) return;

        await RunBusyAsync(async () =>
        {
            var targetDataset = await ResolveTargetDatasetAsync(dialogResult);
            if (targetDataset is null)
            {
                return;
            }

            var targetVersion = await ResolveTargetVersionAsync(targetDataset, dialogResult);
            var destinationFolder = targetDataset.GetVersionFolderPath(targetVersion);

            var importResult = await DatasetFileImporter.ImportWithDialogAsync(
                selectedItems.Select(item => item.FilePath),
                destinationFolder,
                DialogService,
                _videoThumbnailService,
                moveFiles: dialogResult.ImportAction == DatasetImportAction.Move);

            if (importResult.Cancelled)
            {
                return;
            }

            if (dialogResult.ImportAction == DatasetImportAction.Move)
            {
                var movedSet = importResult.ProcessedSourceFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in selectedItems.Where(item => movedSet.Contains(item.FilePath)).ToList())
                {
                    RemoveMediaItem(item);
                }
            }

            targetDataset.RefreshImageInfo();
            _eventAggregator?.PublishImageAdded(new ImageAddedEventArgs
            {
                Dataset = targetDataset,
                AddedImages = []
            });

            ClearSelectionSilent();
            UpdateSelectionState();
        }, "Adding media to dataset...");
    }

    private async Task<DatasetCardViewModel?> ResolveTargetDatasetAsync(AddToDatasetResult dialogResult)
    {
        if (dialogResult.DestinationOption == DatasetDestinationOption.ExistingDataset)
        {
            if (dialogResult.SelectedDataset is null && DialogService is not null)
            {
                await DialogService.ShowMessageAsync(
                    "No Dataset Selected",
                    "Please choose an existing dataset to continue.");
            }
            return dialogResult.SelectedDataset;
        }

        if (DialogService is null || _settingsService is null || _datasetState is null)
        {
            return null;
        }

        var createResult = await DialogService.ShowCreateDatasetDialogAsync(_datasetState.AvailableCategories);
        if (!createResult.Confirmed || string.IsNullOrWhiteSpace(createResult.Name))
        {
            return null;
        }

        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            await DialogService.ShowMessageAsync(
                "Dataset Storage Not Configured",
                "Please configure a dataset storage path in Settings before creating datasets.");
            return null;
        }

        _datasetState.SetStorageConfigured(true);

        var datasetPath = Path.Combine(settings.DatasetStoragePath, createResult.Name);
        if (Directory.Exists(datasetPath))
        {
            await DialogService.ShowMessageAsync(
                "Dataset Already Exists",
                $"A dataset named '{createResult.Name}' already exists.");
            return null;
        }

        Directory.CreateDirectory(datasetPath);
        var v1Path = Path.Combine(datasetPath, "V1");
        Directory.CreateDirectory(v1Path);

        var newDataset = new DatasetCardViewModel
        {
            Name = createResult.Name,
            FolderPath = datasetPath,
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = 0,
            VideoCount = 0,
            CategoryId = createResult.CategoryId,
            CategoryOrder = createResult.CategoryOrder,
            CategoryName = createResult.CategoryName,
            Type = createResult.Type,
            IsNsfw = createResult.IsNsfw
        };

        newDataset.VersionNsfwFlags[1] = createResult.IsNsfw;
        newDataset.SaveMetadata();
        _datasetState.Datasets.Add(newDataset);

        _eventAggregator?.PublishDatasetCreated(new DatasetCreatedEventArgs
        {
            Dataset = newDataset
        });

        return newDataset;
    }

    private async Task<int> ResolveTargetVersionAsync(DatasetCardViewModel dataset, AddToDatasetResult dialogResult)
    {
        if (dialogResult.DestinationOption == DatasetDestinationOption.NewDataset)
        {
            return dataset.CurrentVersion;
        }

        if (dialogResult.VersionOption == DatasetVersionOption.CreateNewVersion)
        {
            var parentVersion = dataset.CurrentVersion;
            return await DatasetVersionUtilities.CreateEmptyVersionAsync(dataset, parentVersion, _eventAggregator);
        }

        return dialogResult.SelectedVersion ?? dataset.CurrentVersion;
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
                items.Add(new GenerationGalleryMediaItemViewModel(file, isVideo, createdAt, root));
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

        UpdateGroupOptionsForSort();
        ApplySortingAndGrouping();

        NoMediaMessage = enabledSourceCount == 0
            ? "No generation gallery folders are enabled. Configure Generation Galleries in Settings to get started."
            : "No media found in enabled generation gallery folders. Check your Generation Galleries in Settings.";

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        _lastClickedItem = null;
        UpdateSelectionState();
    }

    private void ApplySortingAndGrouping()
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

        ApplyGrouping();

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        UpdateSelectionState();
    }

    private void ApplyGrouping()
    {
        GroupedMediaItems.Clear();

        if (MediaItems.Count == 0)
        {
            return;
        }

        if (string.Equals(SelectedGroupOption, "None", StringComparison.OrdinalIgnoreCase))
        {
            var group = new GenerationGalleryGroupViewModel
            {
                ShowHeader = false
            };

            foreach (var item in MediaItems)
            {
                group.Items.Add(item);
            }

            group.UpdateCountText();
            GroupedMediaItems.Add(group);
            return;
        }

        var groupsByKey = new Dictionary<string, GenerationGalleryGroupViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in MediaItems)
        {
            var (key, title) = GetGroupingKey(item);
            if (!groupsByKey.TryGetValue(key, out var group))
            {
                group = new GenerationGalleryGroupViewModel
                {
                    Name = title,
                    ShowHeader = true
                };
                groupsByKey.Add(key, group);
                GroupedMediaItems.Add(group);
            }

            group.Items.Add(item);
        }

        foreach (var group in GroupedMediaItems)
        {
            group.UpdateCountText();
        }
    }

    private void LoadDesignData()
    {
        _allMediaItems.Clear();
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-01.png", false, DateTime.UtcNow.AddDays(-1), "C:\\Images"));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-02.jpg", false, DateTime.UtcNow, "C:\\Images"));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Videos\\Sample-03.mp4", true, DateTime.UtcNow.AddHours(-4), "C:\\Videos"));

        UpdateGroupOptionsForSort();
        ApplySortingAndGrouping();
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
        AddSelectedToDatasetCommand.NotifyCanExecuteChanged();
    }

    private void RemoveMediaItem(GenerationGalleryMediaItemViewModel item)
    {
        _allMediaItems.Remove(item);
        ApplySortingAndGrouping();
    }

    private void UpdateGroupOptionsForSort()
    {
        var options = IsNameSortSelected ? _nameGroupOptions : _dateGroupOptions;

        GroupOptions.Clear();
        foreach (var option in options)
        {
            GroupOptions.Add(option);
        }

        if (!GroupOptions.Contains(SelectedGroupOption))
        {
            SelectedGroupOption = GroupOptions.Contains("None") ? "None" : GroupOptions[0];
        }
    }

    private (string Key, string Title) GetGroupingKey(GenerationGalleryMediaItemViewModel item)
    {
        if (IsNameSortSelected)
        {
            var folderName = GetFolderGroupName(item);
            return (folderName, folderName);
        }

        var localDate = item.CreatedAtUtc.ToLocalTime();

        if (string.Equals(SelectedGroupOption, "Day", StringComparison.OrdinalIgnoreCase))
        {
            var date = localDate.Date;
            return (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture));
        }

        if (string.Equals(SelectedGroupOption, "Week", StringComparison.OrdinalIgnoreCase))
        {
            var weekYear = ISOWeek.GetYear(localDate);
            var weekNumber = ISOWeek.GetWeekOfYear(localDate);
            var key = $"{weekYear}-W{weekNumber:D2}";
            return (key, $"Week {weekNumber} ({weekYear})");
        }

        if (string.Equals(SelectedGroupOption, "Month", StringComparison.OrdinalIgnoreCase))
        {
            var key = localDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return (key, localDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        }

        if (string.Equals(SelectedGroupOption, "Year", StringComparison.OrdinalIgnoreCase))
        {
            var year = localDate.Year.ToString(CultureInfo.InvariantCulture);
            return (year, year);
        }

        return (string.Empty, string.Empty);
    }

    private string GetFolderGroupName(GenerationGalleryMediaItemViewModel item)
    {
        var directory = Path.GetDirectoryName(item.FilePath) ?? string.Empty;
        var sourceRoot = item.SourceRoot;

        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(directory))
        {
            return Path.GetFileName(directory);
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(sourceRoot, directory);
        }
        catch (ArgumentException)
        {
            return Path.GetFileName(directory);
        }

        if (relative == "." || string.IsNullOrEmpty(relative))
        {
            return Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (IncludeSubfolders)
        {
            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstSegment) ? relative : firstSegment;
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
}
