using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels.Controls;
using Serilog;
using System.Text.Json;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Generation Gallery mosaic gallery.
/// </summary>
public partial class GenerationGalleryViewModel : BusyViewModelBase, IThumbnailAware
{
    private static readonly ILogger Logger = Log.ForContext<GenerationGalleryViewModel>();
    private readonly IAppSettingsService? _settingsService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly IDatasetState? _datasetState;
    private readonly IVideoThumbnailService? _videoThumbnailService;
    private readonly IThumbnailOrchestrator? _thumbnailOrchestrator;
    private readonly IImageFavoritesService? _favoritesService;
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
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        IImageFavoritesService? favoritesService = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _datasetState = datasetState ?? throw new ArgumentNullException(nameof(datasetState));
        _videoThumbnailService = videoThumbnailService;
        _thumbnailOrchestrator = thumbnailOrchestrator;
        _favoritesService = favoritesService;

        // Both toolbars are the SAME reusable component the workflow result strips use, so "Add
        // Selected To…" and "Send Selected To…" behave identically everywhere. They're split into two
        // instances only because they need different source sets: Add takes ALL selected media (datasets
        // hold videos too), while the Send destinations are image-only. DialogService is propagated by
        // the view once a window exists.
        AddActions = new ImageActionsViewModel(_datasetState, _eventAggregator, _videoThumbnailService, _settingsService)
        {
            ShowSendToImageEditor = false,
            ShowSendToComparer = false,
            ShowSendToBatchUpscale = false,
            ShowSendToBatchCrop = false,
            ShowSendToCaptioning = false,
            ShowSendToWorkflows = false,
            PathProvider = () => Task.FromResult(new ImageActionPaths(
                MediaItems.Where(item => item.IsSelected)
                          .Select(item => item.FilePath)
                          .Where(File.Exists)
                          .ToList())),
        };
        // A move relocates the files out of the gallery folder; drop those tiles from the view.
        AddActions.FilesMoved += OnActionsFilesMoved;

        SendActions = new ImageActionsViewModel(_datasetState, _eventAggregator, _videoThumbnailService, _settingsService)
        {
            ShowAddToDataset = false,
            ShowAddToTrainingRun = false,
            PathProvider = () => Task.FromResult(new ImageActionPaths(
                MediaItems.Where(item => item.IsSelected && item.IsImage)
                          .Select(item => item.FilePath)
                          .Where(File.Exists)
                          .ToList())),
        };

        _eventAggregator.SettingsSaved += OnSettingsSaved;
        UpdateGroupingOptions();
    }

    /// <summary>
    /// The reusable "Add Selected To…" actions (Dataset / Training Run). Operates on all selected media
    /// (images and videos). Null in the design-time constructor (no services).
    /// </summary>
    public ImageActionsViewModel? AddActions { get; }

    /// <summary>
    /// The reusable "Send Selected To…" actions (Image Editor / Comparer / Batch Upscale / Batch Crop /
    /// Captioning / Workflows). Operates on selected images only. Null in the design-time constructor.
    /// Enablement of both toolbars tracks the current selection via <see cref="UpdateSelectionState"/>.
    /// </summary>
    public ImageActionsViewModel? SendActions { get; }

    /// <summary>Drops moved-away tiles from the view after an Add "Move" relocates them.</summary>
    private void OnActionsFilesMoved(IReadOnlyList<string> movedPaths)
    {
        var moved = movedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in MediaItems.Where(item => moved.Contains(item.FilePath)).ToList())
            RemoveMediaItem(item);

        ClearSelectionSilent();
        UpdateSelectionState();
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

    public IReadOnlyList<string> LayoutModes { get; } = ["Showcase", "Grid"];

    public string ImageExtensionsDisplay => SupportedMediaTypes.ImageExtensionsDisplay;

    public string VideoExtensionsDisplay => SupportedMediaTypes.VideoExtensionsDisplay;

    [ObservableProperty]
    private string _selectedSortOption = "Creation date";

    [ObservableProperty]
    private string _selectedGroupingOption = "None";

    [ObservableProperty]
    private string _selectedDateFilter = "Last 3 Months";

    [ObservableProperty]
    private double _tileHeight = 220;

    [ObservableProperty]
    private string? _noMediaMessage;

    [ObservableProperty]
    private bool _includeSubFolders = true;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showFavoritesOnly;

    private string _selectedLayoutMode = "Showcase";

    public string SelectedLayoutMode
    {
        get => _selectedLayoutMode;
        set
        {
            if (SetProperty(ref _selectedLayoutMode, value))
            {
                OnPropertyChanged(nameof(IsShowcaseLayout));
            }
        }
    }

    /// <summary>
    /// True when the gallery uses the Showcase (aspect-ratio preserving) layout.
    /// False selects the classic square Grid layout.
    /// </summary>
    public bool IsShowcaseLayout => SelectedLayoutMode == "Showcase";

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

    public bool HasFavorites => MediaItems.Any(item => item.IsFavorite);

    /// <summary>
    /// True when at least one selected item is a favorite.
    /// Used to decide whether the bulk action should unmark (any favorite present)
    /// or mark (no favorites in selection).
    /// </summary>
    public bool AnySelectedIsFavorite =>
        HasSelection && MediaItems.Any(item => item.IsSelected && item.IsFavorite);

    /// <summary>
    /// Button text for the bulk favorites toggle.
    /// Shows "☆ Unmark Favorites" when any selected item is a favorite (including mixed);
    /// shows "★ Mark as Favorites" only when none of the selected items are favorites.
    /// </summary>
    public string ToggleFavoritesButtonText =>
        AnySelectedIsFavorite ? "☆ Unmark Favorites" : "★ Mark as Favorites";

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
            var includeSubFolders = IncludeSubFolders;

            // Offload the recursive folder scan (per-file IO syscalls + item creation)
            // to the thread pool so the UI thread stays responsive; with large auto-
            // registered output folders the inline scan froze the window for ~20s at
            // startup (issue #397). RunBusyAsync itself does not offload.
            var mediaItems = await Task.Run(() => CollectMediaItemsAsync(enabledPaths, includeSubFolders));
            await ApplyMediaItemsAsync(mediaItems, enabledPaths.Count);

            // Fire-and-forget: generate missing video thumbnails after gallery is displayed
            StartBackgroundVideoThumbnailGeneration(mediaItems);
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

    partial void OnShowFavoritesOnlyChanged(bool value)
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

    /// <summary>
    /// Toggles the favorite state of the given media item and persists the change.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFavoriteAsync(GenerationGalleryMediaItemViewModel? item)
    {
        if (item is null || _favoritesService is null) return;

        var newState = await _favoritesService.ToggleFavoriteAsync(item.FilePath);
        item.IsFavorite = newState;

        OnPropertyChanged(nameof(HasFavorites));
        SelectAllFavoritesCommand.NotifyCanExecuteChanged();

        if (ShowFavoritesOnly && !newState)
        {
            ApplySortingAndGrouping();
        }
    }

    /// <summary>
    /// Selects all items that are marked as favorites.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasFavorites))]
    private void SelectAllFavorites()
    {
        ClearSelectionSilent();
        foreach (var item in MediaItems)
        {
            if (item.IsFavorite)
            {
                item.IsSelected = true;
            }
        }

        UpdateSelectionState();
    }

    /// <summary>
    /// Toggles favorites for all selected items.
    /// If any selected item is a favorite (including mixed), unmarks all.
    /// If none are favorites, marks all as favorites.
    /// This means a mixed selection requires two clicks to mark all as favorites.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleSelectedFavoritesAsync()
    {
        if (_favoritesService is null) return;

        var selectedItems = MediaItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0) return;

        // Any favorite present → unmark all; no favorites → mark all
        var newState = !selectedItems.Any(item => item.IsFavorite);

        foreach (var item in selectedItems)
        {
            await _favoritesService.SetFavoriteAsync(item.FilePath, newState);
            item.IsFavorite = newState;
        }

        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(AnySelectedIsFavorite));
        OnPropertyChanged(nameof(ToggleFavoritesButtonText));
        SelectAllFavoritesCommand.NotifyCanExecuteChanged();

        if (ShowFavoritesOnly && !newState)
        {
            ApplySortingAndGrouping();
        }
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

    private async Task OpenImageViewerAtIndexAsync(int index)
    {
        if (DialogService is null || MediaItems.Count == 0) return;
        if (index < 0 || index >= MediaItems.Count) return;

        var viewerImages = new ObservableCollection<DatasetImageViewModel>(
            MediaItems.Select(item => DatasetImageViewModel.FromFile(item.FilePath)));

        // Build favorite callbacks only when the service is available
        Func<string, Task<bool>>? toggleFavorite = null;
        Func<string, bool>? isFavoriteCheck = null;

        if (_favoritesService is not null)
        {
            toggleFavorite = async filePath =>
            {
                var newState = await _favoritesService.ToggleFavoriteAsync(filePath);

                // Sync the gallery item state
                var galleryItem = MediaItems.FirstOrDefault(
                    item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (galleryItem is not null)
                {
                    galleryItem.IsFavorite = newState;
                }

                OnPropertyChanged(nameof(HasFavorites));
                SelectAllFavoritesCommand.NotifyCanExecuteChanged();
                return newState;
            };

            isFavoriteCheck = filePath =>
                MediaItems.FirstOrDefault(
                    item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    ?.IsFavorite ?? false;
        }

        await DialogService.ShowImageViewerDialogAsync(
            viewerImages,
            index,
            showRatingControls: false,
            onToggleFavorite: toggleFavorite,
            isFavoriteCheck: isFavoriteCheck,
            videoThumbnailService: _videoThumbnailService);
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

    private async Task<List<GenerationGalleryMediaItemViewModel>> CollectMediaItemsAsync(IEnumerable<string> paths, bool includeSubFolders)
    {
        var items = new List<GenerationGalleryMediaItemViewModel>();

        // Collect favorites per folder for bulk lookup
        var favoriteSets = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

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
                var item = new GenerationGalleryMediaItemViewModel(
                    file, isVideo, createdAt, folderGroupName,
                    _thumbnailOrchestrator, OwnerToken);

                if (_favoritesService is not null)
                {
                    var folder = Path.GetDirectoryName(file)!;
                    if (!favoriteSets.TryGetValue(folder, out var favSet))
                    {
                        favSet = await _favoritesService.GetFavoritesAsync(folder).ConfigureAwait(false);
                        favoriteSets[folder] = favSet;
                    }

                    item.IsFavorite = favSet.Contains(Path.GetFileName(file));
                }

                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>
    /// Generates missing video thumbnails in the background without blocking the gallery.
    /// When a thumbnail is ready, the corresponding item's cache entry is invalidated
    /// and its thumbnail is reloaded so the placeholder is replaced live.
    /// </summary>
    private void StartBackgroundVideoThumbnailGeneration(IReadOnlyList<GenerationGalleryMediaItemViewModel> items)
    {
        if (_videoThumbnailService is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // The File.Exists probe per video is blocking IO, so it belongs on the
            // thread pool too — not on the UI thread that calls this method (#397).
            var videoItems = items
                .Where(i => i.IsVideo && !File.Exists(MediaFileExtensions.GetVideoThumbnailPath(i.FilePath)))
                .ToList();

            if (videoItems.Count == 0)
            {
                return;
            }

            // Limit concurrency to avoid saturating CPU/disk with FFmpeg processes
            using var semaphore = new SemaphoreSlim(2);

            var tasks = videoItems.Select(async item =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var result = await _videoThumbnailService.GenerateThumbnailAsync(item.FilePath).ConfigureAwait(false);

                    if (result.Success)
                    {
                        // Invalidate the cached placeholder so the real thumbnail is loaded
                        _thumbnailOrchestrator?.Invalidate(item.FilePath);
                        item.ReloadThumbnail();
                    }
                    else
                    {
                        Logger.Warning("Video thumbnail generation failed for {Path}: {Error}",
                            item.FilePath, result.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Video thumbnail generation threw for {Path}", item.FilePath);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        });
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
                // Skip .thumbnails subfolder used for video thumbnail storage
                if (string.Equals(Path.GetFileName(directory), MediaFileExtensions.ThumbnailsSubfolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                pending.Push(directory);
            }
        }
    }

    private async Task ApplyMediaItemsAsync(List<GenerationGalleryMediaItemViewModel> items, int enabledSourceCount)
    {
        // When no Avalonia Application is running (e.g. unit tests) the static
        // Dispatcher.UIThread is bound to whichever thread first touched it and
        // has no message pump on subsequent test threads. Posting to it would
        // hang the test indefinitely, so we execute inline instead.
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
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
        var showFavoritesOnly = ShowFavoritesOnly;

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

            if (showFavoritesOnly)
            {
                filtered = filtered.Where(item => item.IsFavorite);
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
        OnPropertyChanged(nameof(HasFavorites));
        if (AddActions is not null)
            AddActions.CanAct = HasSelection;
        if (SendActions is not null)
            SendActions.CanAct = HasSelection;
        OpenFolderInExplorerCommand.NotifyCanExecuteChanged();
        SelectAllFavoritesCommand.NotifyCanExecuteChanged();
        ToggleSelectedFavoritesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AnySelectedIsFavorite));
        OnPropertyChanged(nameof(ToggleFavoritesButtonText));
    }

    private void RemoveMediaItem(GenerationGalleryMediaItemViewModel item)
    {
        _allMediaItems.Remove(item);
        MediaItems.Remove(item);
        VisibleMediaItems.Remove(item);
        UpdateGroupedMediaItems(MediaItems.ToList());
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        OnPropertyChanged(nameof(HasMoreItems));
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
