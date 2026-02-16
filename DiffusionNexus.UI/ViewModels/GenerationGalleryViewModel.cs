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
public partial class GenerationGalleryViewModel : BusyViewModelBase, IThumbnailAware
{
    private readonly IAppSettingsService? _settingsService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly IDatasetState? _datasetState;
    private readonly IVideoThumbnailService? _videoThumbnailService;
    private readonly IThumbnailOrchestrator? _thumbnailOrchestrator;
    private readonly List<GenerationGalleryMediaItemViewModel> _allMediaItems = [];
    private GenerationGalleryMediaItemViewModel? _lastClickedItem;
    private int _selectionCount;
    private bool _isUpdatingGroupingOptions;
    private Task _lastSortTask = Task.CompletedTask;
    private bool _isLoadingMore;

    /// <summary>
    /// Number of items to render in the first batch when the gallery opens.
    /// Keeps the initial UI layout fast (&lt;100ms) regardless of total item count.
    /// </summary>
    private const int InitialPageSize = 50;

    /// <summary>
    /// Number of additional items to add when the user scrolls near the bottom.
    /// </summary>
    private const int PageIncrement = 50;

    public GenerationGalleryViewModel()
    {
        _settingsService = null;
        _eventAggregator = null;
        _datasetState = null;
        _videoThumbnailService = null;
        UpdateGroupingOptions();
        LoadDesignData();
    }

    public GenerationGalleryViewModel(
        IAppSettingsService settingsService,
        IDatasetEventAggregator eventAggregator,
        IDatasetState datasetState,
        IVideoThumbnailService? videoThumbnailService,
        IThumbnailOrchestrator? thumbnailOrchestrator = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _datasetState = datasetState ?? throw new ArgumentNullException(nameof(datasetState));
        _videoThumbnailService = videoThumbnailService;
        _thumbnailOrchestrator = thumbnailOrchestrator;
        
        _eventAggregator.SettingsSaved += OnSettingsSaved;
        UpdateGroupingOptions();
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        // Reload gallery when settings change (new folders might be added)
        LoadMediaCommand.Execute(null);
    }

    /// <summary>
    /// Gets or sets the process launcher for opening folders in Explorer.
    /// Defaults to <see cref="DefaultProcessLauncher"/> when not explicitly set.
    /// </summary>
    public IProcessLauncher ProcessLauncher { get; set; } = new DefaultProcessLauncher();

    public BatchObservableCollection<GenerationGalleryMediaItemViewModel> MediaItems { get; } = [];

    /// <summary>
    /// The subset of <see cref="MediaItems"/> currently materialised in the UI.
    /// Starts with <see cref="InitialPageSize"/> items and grows as the user scrolls.
    /// Binds to the non-grouped <c>ItemsControl</c>.
    /// </summary>
    public BatchObservableCollection<GenerationGalleryMediaItemViewModel> VisibleMediaItems { get; } = [];

    public BatchObservableCollection<GenerationGalleryGroupViewModel> GroupedMediaItems { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } = ["Name", "Creation date"];

    public IReadOnlyList<string> DateFilterOptions { get; } =
    [
        "Last 10 Days",
        "Last 30 Days",
        "Last 3 Months",
        "Last 6 Months",
        "This Year",
        "All Time"
    ];

    public ObservableCollection<string> GroupingOptions { get; } = [];

    public string ImageExtensionsDisplay => SupportedMediaTypes.ImageExtensionsDisplay;

    public string VideoExtensionsDisplay => SupportedMediaTypes.VideoExtensionsDisplay;

    [ObservableProperty]
    private string _selectedSortOption = "Creation date";

    [ObservableProperty]
    private string _selectedGroupingOption = "None";

    [ObservableProperty]
    private string _selectedDateFilter = "Last 3 Months";

    [ObservableProperty]
    private double _tileWidth = 220;

    [ObservableProperty]
    private string? _noMediaMessage;

    [ObservableProperty]
    private bool _includeSubFolders = true;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public int SelectionCount
    {
        get => _selectionCount;
        private set => SetProperty(ref _selectionCount, value);
    }

    public bool HasSelection => SelectionCount > 0;

    public bool HasMultipleImagesSelected => SelectionCount >= 2 && MediaItems.Where(item => item.IsSelected && item.IsImage).Take(2).Count() >= 2;

    public string SelectionText => SelectionCount == 1 ? "1 selected" : $"{SelectionCount} selected";

    public bool HasMedia => MediaItems.Count > 0;

    public bool HasNoMedia => !HasMedia;

    public bool IsGroupingEnabled => !string.Equals(SelectedGroupingOption, "None", StringComparison.OrdinalIgnoreCase);

    #region IThumbnailAware

    /// <inheritdoc />
    public ThumbnailOwnerToken OwnerToken { get; } = new("GenerationGallery");

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

            var mediaItems = await Task.Run(() => CollectMediaItems(enabledPaths, IncludeSubFolders));
            await ApplyMediaItemsAsync(mediaItems, enabledPaths.Count);
        }, "Loading gallery...");
    }

    partial void OnSelectedSortOptionChanged(string value)
    {
        UpdateGroupingOptions();
        ApplySortingAndGrouping();
    }

    partial void OnSelectedGroupingOptionChanged(string value)
    {
        OnPropertyChanged(nameof(IsGroupingEnabled));
        if (_isUpdatingGroupingOptions)
        {
            return;
        }

        ApplySortingAndGrouping();
    }

    partial void OnSelectedDateFilterChanged(string value)
    {
        ApplySortingAndGrouping();
    }

    partial void OnIncludeSubFoldersChanged(bool value)
    {
        LoadMediaCommand.Execute(null);
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySortingAndGrouping();
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

    [RelayCommand]
    private async Task OpenViewerAsync()
    {
        if (DialogService is null || MediaItems.Count == 0) return;

        var startIndex = GetDefaultViewerIndex();
        await OpenImageViewerAtIndexAsync(startIndex);
    }

    [RelayCommand]
    private async Task OpenImageViewerAsync(GenerationGalleryMediaItemViewModel? item)
    {
        if (DialogService is null || item is null) return;

        var index = MediaItems.IndexOf(item);
        if (index < 0) return;

        await OpenImageViewerAtIndexAsync(index);
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

    private async Task OpenImageViewerAtIndexAsync(int index)
    {
        if (DialogService is null || MediaItems.Count == 0) return;
        if (index < 0 || index >= MediaItems.Count) return;

        var viewerImages = new ObservableCollection<DatasetImageViewModel>(
            MediaItems.Select(item => DatasetImageViewModel.FromFile(item.FilePath)));

        await DialogService.ShowImageViewerDialogAsync(
            viewerImages,
            index,
            showRatingControls: false);
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

    private List<GenerationGalleryMediaItemViewModel> CollectMediaItems(IEnumerable<string> paths, bool includeSubFolders)
    {
        var items = new List<GenerationGalleryMediaItemViewModel>();
        foreach (var root in paths)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in EnumerateMediaFiles(root, includeSubFolders))
            {
                var isVideo = SupportedMediaTypes.IsVideoFile(file);
                var createdAt = File.GetCreationTimeUtc(file);
                var folderGroupName = GetFolderGroupName(root, file);
                items.Add(new GenerationGalleryMediaItemViewModel(
                    file, isVideo, createdAt, folderGroupName,
                    _thumbnailOrchestrator, OwnerToken));
            }
        }

        return items;
    }

    private static IEnumerable<string> EnumerateMediaFiles(string root, bool includeSubFolders)
    {
        if (!includeSubFolders)
        {
            IEnumerable<string> files = [];
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }
            catch (DirectoryNotFoundException)
            {
                yield break;
            }
            catch (IOException)
            {
                yield break;
            }

            foreach (var file in files)
            {
                if (SupportedMediaTypes.IsMediaFile(file))
                {
                    yield return file;
                }
            }

            yield break;
        }

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
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() => ApplyMediaItems(items, enabledSourceCount));
        }

        // Wait for the sort/filter/group to finish before returning to the caller
        await _lastSortTask;
    }

    private void ApplyMediaItems(List<GenerationGalleryMediaItemViewModel> items, int enabledSourceCount)
    {
        _allMediaItems.Clear();
        _allMediaItems.AddRange(items);

        ApplySortingAndGrouping();

        NoMediaMessage = enabledSourceCount == 0
            ? "No generation gallery folders are enabled. Configure Generation Galleries in Settings to get started."
            : "No media found in enabled generation gallery folders. Check your Generation Galleries in Settings.";

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        _lastClickedItem = null;
        UpdateSelectionState();
    }

    /// <summary>
    /// Waits for any in-progress sort/filter/group operation to complete.
    /// Intended for test support.
    /// </summary>
    public Task WaitForSortingAsync() => _lastSortTask;

    private void ApplySortingAndGrouping()
    {
        _lastSortTask = ApplySortingAndGroupingAsync();
    }

    /// <summary>
    /// Performs sorting, filtering and grouping asynchronously.
    /// The heavy LINQ work runs on a thread-pool thread via <see cref="Task.Run"/>.
    /// The final <see cref="BatchObservableCollection{T}.ReplaceAll"/> runs back on
    /// the calling context (UI thread) so no <c>Dispatcher.InvokeAsync</c> is needed,
    /// avoiding deadlocks during startup or from synchronous property-change handlers.
    /// </summary>
    private async Task ApplySortingAndGroupingAsync()
    {
        // Capture current filter/sort state for the background thread
        var allItems = _allMediaItems;
        var dateFilter = SelectedDateFilter;
        var searchText = SearchText;
        var sortOption = SelectedSortOption;
        var groupingOption = SelectedGroupingOption;
        var isGroupingEnabled = IsGroupingEnabled;

        // Run sorting, filtering, and group creation on a background thread
        var (sortedList, groups) = await Task.Run(() =>
        {
            IEnumerable<GenerationGalleryMediaItemViewModel> filtered = allItems;

            var cutoff = GetDateFilterCutoff(dateFilter);
            if (cutoff.HasValue)
            {
                filtered = filtered.Where(item => item.CreatedAtUtc >= cutoff.Value);
            }

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                filtered = filtered.Where(item =>
                    item.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            IEnumerable<GenerationGalleryMediaItemViewModel> sorted = filtered;

            if (string.Equals(sortOption, "Creation date", StringComparison.OrdinalIgnoreCase))
            {
                sorted = sorted.OrderByDescending(item => item.CreatedAtUtc)
                    .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                sorted = sorted.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase);
            }

            var resultList = sorted.ToList();

            // Build groups on background thread too
            List<GenerationGalleryGroupViewModel>? resultGroups = null;
            if (isGroupingEnabled)
            {
                resultGroups = (groupingOption switch
                {
                    "Day" => CreateDateGroups(resultList),
                    "Week" => CreateWeekGroups(resultList),
                    "Month" => CreateMonthGroups(resultList),
                    "Year" => CreateYearGroups(resultList),
                    "Folder" => CreateFolderGroups(resultList),
                    _ => Enumerable.Empty<GenerationGalleryGroupViewModel>()
                }).ToList();
            }

            return (resultList, resultGroups);
        });

        // Back on the original context (UI thread) — apply results directly
        ApplySortedResults(sortedList, groups);
    }

    private void ApplySortedResults(
        List<GenerationGalleryMediaItemViewModel> sortedList,
        List<GenerationGalleryGroupViewModel>? groups)
    {
        MediaItems.ReplaceAll(sortedList);

        // Only materialise the first page in the UI — the rest loads on scroll
        var initialPage = sortedList.Count <= InitialPageSize
            ? sortedList
            : sortedList.GetRange(0, InitialPageSize);
        VisibleMediaItems.ReplaceAll(initialPage);

        if (groups is not null)
        {
            GroupedMediaItems.ReplaceAll(groups);
        }
        else if (GroupedMediaItems.Count > 0)
        {
            GroupedMediaItems.ReplaceAll([]);
        }

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        OnPropertyChanged(nameof(HasMoreItems));
        UpdateSelectionState();
    }

    /// <summary>
    /// True when <see cref="VisibleMediaItems"/> does not yet contain all <see cref="MediaItems"/>.
    /// Used by the view to decide whether to request more items on scroll.
    /// </summary>
    public bool HasMoreItems => VisibleMediaItems.Count < MediaItems.Count;

    /// <summary>
    /// Appends the next page of items to <see cref="VisibleMediaItems"/>.
    /// Called by the view when the user scrolls near the bottom.
    /// </summary>
    public void LoadMoreItems()
    {
        if (_isLoadingMore || !HasMoreItems) return;
        _isLoadingMore = true;

        var currentCount = VisibleMediaItems.Count;
        var nextBatchEnd = Math.Min(currentCount + PageIncrement, MediaItems.Count);

        for (var i = currentCount; i < nextBatchEnd; i++)
        {
            VisibleMediaItems.Add(MediaItems[i]);
        }

        OnPropertyChanged(nameof(HasMoreItems));
        _isLoadingMore = false;
    }

    private void LoadDesignData()
    {
        _allMediaItems.Clear();
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-01.png", false, DateTime.UtcNow.AddDays(-1), "Images"));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Images\\Sample-02.jpg", false, DateTime.UtcNow, "Images"));
        _allMediaItems.Add(new GenerationGalleryMediaItemViewModel("C:\\Videos\\Sample-03.mp4", true, DateTime.UtcNow.AddHours(-4), "Videos"));

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
        OnPropertyChanged(nameof(HasMultipleImagesSelected));
        OnPropertyChanged(nameof(SelectionText));
        AddSelectedToDatasetCommand.NotifyCanExecuteChanged();
        SendSelectedToImageEditCommand.NotifyCanExecuteChanged();
        SendSelectedToImageComparerCommand.NotifyCanExecuteChanged();
        OpenFolderInExplorerCommand.NotifyCanExecuteChanged();
    }

    private void RemoveMediaItem(GenerationGalleryMediaItemViewModel item)
    {
        _allMediaItems.Remove(item);
        MediaItems.Remove(item);
        UpdateGroupedMediaItems(MediaItems.ToList());
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
    }

    private int GetDefaultViewerIndex()
    {
        for (var i = 0; i < MediaItems.Count; i++)
        {
            if (MediaItems[i].IsSelected)
            {
                return i;
            }
        }

        return 0;
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

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SendSelectedToImageEditAsync()
    {
        if (_eventAggregator is null)
        {
            return;
        }

        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            return;
        }

        var imageItems = selectedItems.Where(item => item.IsImage).ToList();
        if (imageItems.Count == 0)
        {
            if (DialogService is not null)
            {
                await DialogService.ShowMessageAsync(
                    "No Images Selected",
                    "The Image Editor only supports images. Please select at least one image.");
            }
            return;
        }

        var editorImages = imageItems
            .Select(item => DatasetImageViewModel.FromFile(item.FilePath, _eventAggregator))
            .ToList();

        var tempDataset = new DatasetCardViewModel
        {
            Name = "Temp Dataset",
            FolderPath = "TEMP://GenerationGallery",
            IsVersionedStructure = true,
            CurrentVersion = 1,
            TotalVersions = 1,
            ImageCount = editorImages.Count,
            TotalImageCountAllVersions = editorImages.Count,
            IsTemporary = true
        };

        _eventAggregator.PublishNavigateToImageEditor(new NavigateToImageEditorEventArgs
        {
            Dataset = tempDataset,
            Image = editorImages[0],
            Images = editorImages
        });
    }

    [RelayCommand(CanExecute = nameof(HasMultipleImagesSelected))]
    private async Task SendSelectedToImageComparerAsync()
    {
        if (_eventAggregator is null)
        {
            return;
        }

        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count < 2)
        {
            if (DialogService is not null)
            {
                await DialogService.ShowMessageAsync(
                    "Selection Required",
                    "Please select at least 2 images to compare.");
            }
            return;
        }

        var imageItems = selectedItems.Where(item => item.IsImage).ToList();
        if (imageItems.Count < 2)
        {
            if (DialogService is not null)
            {
                await DialogService.ShowMessageAsync(
                    "Not Enough Images Selected",
                    "The Image Comparer requires at least 2 images. Please select at least 2 images (videos are not supported).");
            }
            return;
        }

        var imagePaths = imageItems.Select(item => item.FilePath).ToList();

        _eventAggregator.PublishNavigateToImageComparer(new NavigateToImageComparerEventArgs
        {
            ImagePaths = imagePaths
        });
    }

    /// <summary>
    /// Returns the file paths of all currently selected media items.
    /// Used by the View for clipboard copy and drag-and-drop operations.
    /// </summary>
    public IReadOnlyList<string> GetSelectedFilePaths()
    {
        return MediaItems
            .Where(item => item.IsSelected)
            .Select(item => item.FilePath)
            .ToList();
    }

    /// <summary>
    /// Opens the containing folder(s) of the selected image(s) in Windows Explorer.
    /// If multiple origins exist, each is opened in a separate window.
    /// Shows a confirmation dialog when more than 3 distinct folders would be opened.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenFolderInExplorerAsync()
    {
        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0) return;

        var distinctFolders = selectedItems
            .Select(item => Path.GetDirectoryName(item.FilePath))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctFolders.Count == 0) return;

        if (distinctFolders.Count > 3 && DialogService is not null)
        {
            var confirm = await DialogService.ShowConfirmAsync(
                "Open Multiple Folders",
                $"This will open {distinctFolders.Count} Explorer windows. Do you want to continue?");

            if (!confirm) return;
        }

        foreach (var folder in distinctFolders)
        {
            try
            {
                if (distinctFolders.Count == 1 && selectedItems.Count == 1)
                {
                    ProcessLauncher.OpenFolderAndSelectFile(selectedItems[0].FilePath);
                }
                else
                {
                    ProcessLauncher.OpenFolder(folder!);
                }
            }
            catch
            {
                // Ignore errors opening Explorer - not critical
            }
        }
    }

    private void UpdateGroupingOptions()
    {
        _isUpdatingGroupingOptions = true;
        GroupingOptions.Clear();

        IEnumerable<string> options = string.Equals(SelectedSortOption, "Creation date", StringComparison.OrdinalIgnoreCase)
            ? ["Day", "Week", "Month", "Year", "None"]
            : ["Folder", "None"];

        foreach (var option in options)
        {
            GroupingOptions.Add(option);
        }

        if (!GroupingOptions.Any(option => string.Equals(option, SelectedGroupingOption, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedGroupingOption = "None";
        }

        _isUpdatingGroupingOptions = false;
        OnPropertyChanged(nameof(IsGroupingEnabled));
    }

    private void UpdateGroupedMediaItems(IReadOnlyList<GenerationGalleryMediaItemViewModel> sortedItems)
    {
        if (!IsGroupingEnabled)
        {
            if (GroupedMediaItems.Count > 0)
            {
                GroupedMediaItems.ReplaceAll([]);
            }
            return;
        }

        var groups = (SelectedGroupingOption switch
        {
            "Day" => CreateDateGroups(sortedItems),
            "Week" => CreateWeekGroups(sortedItems),
            "Month" => CreateMonthGroups(sortedItems),
            "Year" => CreateYearGroups(sortedItems),
            "Folder" => CreateFolderGroups(sortedItems),
            _ => Enumerable.Empty<GenerationGalleryGroupViewModel>()
        }).ToList();

        GroupedMediaItems.ReplaceAll(groups);
    }

    private static IEnumerable<GenerationGalleryGroupViewModel> CreateDateGroups(IEnumerable<GenerationGalleryMediaItemViewModel> items)
    {
        return items
            .GroupBy(item => item.CreatedAtUtc.ToLocalTime().Date)
            .OrderByDescending(group => group.Key)
            .Select(group => new GenerationGalleryGroupViewModel(
                group.Key.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture),
                group));
    }

    private static IEnumerable<GenerationGalleryGroupViewModel> CreateWeekGroups(IEnumerable<GenerationGalleryMediaItemViewModel> items)
    {
        return items
            .GroupBy(item =>
            {
                var local = item.CreatedAtUtc.ToLocalTime();
                return (Year: ISOWeek.GetYear(local), Week: ISOWeek.GetWeekOfYear(local));
            })
            .OrderByDescending(group => group.Key.Year)
            .ThenByDescending(group => group.Key.Week)
            .Select(group =>
            {
                var label = $"Week {group.Key.Week} ({group.Key.Year})";
                return new GenerationGalleryGroupViewModel(label, group);
            });
    }

    private static IEnumerable<GenerationGalleryGroupViewModel> CreateMonthGroups(IEnumerable<GenerationGalleryMediaItemViewModel> items)
    {
        return items
            .GroupBy(item =>
            {
                var local = item.CreatedAtUtc.ToLocalTime();
                return new DateTime(local.Year, local.Month, 1);
            })
            .OrderByDescending(group => group.Key)
            .Select(group => new GenerationGalleryGroupViewModel(
                group.Key.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                group));
    }

    private static IEnumerable<GenerationGalleryGroupViewModel> CreateYearGroups(IEnumerable<GenerationGalleryMediaItemViewModel> items)
    {
        return items
            .GroupBy(item => item.CreatedAtUtc.ToLocalTime().Year)
            .OrderByDescending(group => group.Key)
            .Select(group => new GenerationGalleryGroupViewModel(group.Key.ToString(CultureInfo.CurrentCulture), group));
    }

    private static IEnumerable<GenerationGalleryGroupViewModel> CreateFolderGroups(IEnumerable<GenerationGalleryMediaItemViewModel> items)
    {
        return items
            .GroupBy(item => item.FolderGroupName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GenerationGalleryGroupViewModel(group.Key, group));
    }

    private static string GetFolderGroupName(string root, string filePath)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootName = Path.GetFileName(trimmedRoot);
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = trimmedRoot;
        }

        var relativePath = Path.GetRelativePath(root, filePath);
        var relativeDirectory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == ".")
        {
            return rootName;
        }

        var normalized = relativeDirectory.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            normalized = normalized.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        return $"{rootName}/{normalized}";
    }

    private static DateTime? GetDateFilterCutoff(string filter)
    {
        var now = DateTime.UtcNow;
        return filter switch
        {
            "Last 10 Days" => now.AddDays(-10),
            "Last 30 Days" => now.AddDays(-30),
            "Last 3 Months" => now.AddMonths(-3),
            "Last 6 Months" => now.AddMonths(-6),
            "This Year" => new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => null // "All Time"
        };
    }
}
