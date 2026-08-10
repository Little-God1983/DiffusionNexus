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
    private readonly ITagIndexService? _tagIndexService;
    private readonly List<GenerationGalleryMediaItemViewModel> _allMediaItems = [];
    private GenerationGalleryMediaItemViewModel? _lastClickedItem;
    private int _selectionCount;
    private bool _isUpdatingGroupingOptions;
    private Task _lastSortTask = Task.CompletedTask;
    private bool _isLoadingMore;

    /// <summary>
    /// Cancels the running index build. Non-null only for the duration of one
    /// <see cref="BuildTagIndexAsync"/> call; created and disposed there, and
    /// only ever touched from the UI thread (the build's completion path and
    /// <see cref="CancelTagIndex"/> both run there), so there is no window
    /// where cancelling races the dispose.
    /// </summary>
    private CancellationTokenSource? _tagIndexCts;

    /// <summary>
    /// The chain of fire-and-forget index-prune calls started by
    /// <see cref="RemoveMediaItem"/>. Chained rather than fanned out so a bulk
    /// delete does not open one DB context per file simultaneously, and so
    /// <see cref="WaitForTagIndexPruneAsync"/> can wait for all of them.
    /// </summary>
    private Task _lastTagIndexPruneTask = Task.CompletedTask;

    /// <summary>
    /// How many gallery folders were enabled at the last load. Kept so the
    /// empty-state message can be recomputed on every filter pass, not only
    /// when media is (re)loaded. See <see cref="UpdateNoMediaMessage"/>.
    /// </summary>
    private int _enabledSourceCount;

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
        IImageFavoritesService? favoritesService = null,
        ITagIndexService? tagIndexService = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _datasetState = datasetState ?? throw new ArgumentNullException(nameof(datasetState));
        _videoThumbnailService = videoThumbnailService;
        _thumbnailOrchestrator = thumbnailOrchestrator;
        _favoritesService = favoritesService;
        _tagIndexService = tagIndexService;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexStatusText))]
    private int _indexedImageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexStatusText))]
    [NotifyPropertyChangedFor(nameof(TagCloudHeader))]
    private int _totalGalleryImageCount;

    [ObservableProperty]
    private bool _isAdvancedSearchOpen;

    /// <summary>
    /// True while a tag-index build is running. Reveals the toolbar's Cancel
    /// button and gates <see cref="CancelTagIndexCommand"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelTagIndexCommand))]
    private bool _isIndexingTagIndex;

    /// <summary>
    /// Transient feedback for the last tag-index operation — how a finished
    /// build reports what it did, and the only place a build that failed
    /// outright says so. Follows the convention the rest of the app uses for
    /// this (see <c>ImageActionsViewModel</c> / <c>CivitaiBrowserViewModel</c>):
    /// a nullable string the view shows only while it is non-empty.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNsfwFilterShowAll))]
    [NotifyPropertyChangedFor(nameof(IsNsfwFilterHideNsfw))]
    [NotifyPropertyChangedFor(nameof(IsNsfwFilterNsfwOnly))]
    [NotifyPropertyChangedFor(nameof(HasActiveTagFilters))]
    private NsfwFilterMode _nsfwFilter = NsfwFilterMode.ShowAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredMatchCountText))]
    private int _filteredMatchCount;

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

    public string IndexStatusText => $"{IndexedImageCount:N0} / {TotalGalleryImageCount:N0} indexed";

    public BatchObservableCollection<TagCloudEntryViewModel> TagCloud { get; } = [];

    public ObservableCollection<string> ActiveTagFilters { get; } = [];

    /// <summary>
    /// True when anything in the Advanced Search drawer is narrowing the
    /// gallery. The NSFW mode counts: it filters on its own, without any tag
    /// being selected, so leaving it out would hide the active-filter strip —
    /// and with it the only "Clear filters" affordance — while the gallery is
    /// visibly filtered.
    /// </summary>
    public bool HasActiveTagFilters => ActiveTagFilters.Count > 0 || NsfwFilter != NsfwFilterMode.ShowAll;

    public bool IsNsfwFilterShowAll => NsfwFilter == NsfwFilterMode.ShowAll;
    public bool IsNsfwFilterHideNsfw => NsfwFilter == NsfwFilterMode.HideNsfw;
    public bool IsNsfwFilterNsfwOnly => NsfwFilter == NsfwFilterMode.NsfwOnly;

    public string FilteredMatchCountText => $"{FilteredMatchCount:N0} images match";

    public string TagCloudHeader => $"TAG INDEX — {TotalGalleryImageCount:N0} images · {TagCloud.Count:N0} tags";

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

            TotalGalleryImageCount = mediaItems.Count(i => i.IsImage);
            if (_tagIndexService is not null)
            {
                // Both calls hit tables that can be missing entirely: if the
                // DB was locked when DatabaseRecoveryService ran, it can stamp
                // this feature's migrations as applied without creating the
                // tables, and every later startup then sees nothing pending —
                // permanently. That turns into "SQLite Error 1: no such table"
                // here. RunBusyAsync does not catch, so an escape would be
                // rethrown on the UI thread's synchronization context, i.e. an
                // unhandled exception in Avalonia's dispatcher loop. A broken
                // tag index must cost the user their tag data, not the gallery.
                try
                {
                    IndexedImageCount = await _tagIndexService.GetIndexedCountAsync();

                    // With an empty index the hydration lookup can only come back
                    // empty, so skip the query outright: a user who has never run
                    // "Build Tag Index" should pay nothing for this feature on
                    // every single gallery load (issue #397 territory).
                    if (IndexedImageCount > 0)
                    {
                        await HydrateTagDataAsync(mediaItems);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Tag index unavailable; the gallery loaded without tag data");
                }
            }

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
    private void ToggleAdvancedSearch()
    {
        IsAdvancedSearchOpen = !IsAdvancedSearchOpen;
        if (IsAdvancedSearchOpen)
        {
            _ = RefreshTagCloudAsync();
        }
    }

    [RelayCommand]
    private async Task BuildTagIndexAsync()
    {
        if (_tagIndexService is null) return;

        _tagIndexCts?.Dispose();
        var cts = new CancellationTokenSource();
        _tagIndexCts = cts;
        IsIndexingTagIndex = true;
        StatusMessage = null;

        try
        {
            await RunBusyAsync(async () =>
            {
                // BuildIndexAsync filters to image-only extensions internally (and
                // documents this on its own XML doc) — pass the full mixed
                // image/video list through rather than duplicating that filter here.
                var paths = _allMediaItems.Select(i => i.FilePath).ToList();

                // Constructed here, on the calling (UI) thread, so Progress<T>
                // captures the UI SynchronizationContext and marshals every
                // callback back to it even though the build below runs on the
                // thread pool — no Dispatcher.UIThread.Post needed. Covered by
                // BuildTagIndexCommand_RoutesProgressBackThroughTheCapturedSynchronizationContext.
                var progress = new Progress<TagIndexBuildProgress>(p =>
                {
                    // StatusMessage is phase text ("Downloading tagger model…")
                    // and wins outright: the download runs for minutes before a
                    // single file is touched, and a handler that only asked
                    // whether CurrentFile was null sat on "Indexing images… 0/N"
                    // the whole time.
                    BusyMessage = p.StatusMessage
                        ?? (p.CurrentFile is not null
                            ? $"Indexing images… {p.Completed:N0}/{p.Total:N0}"
                            : "Finalizing index…");
                });

                TagIndexBuildResult? result = null;
                var cancelled = false;
                try
                {
                    // Offloaded for exactly the reason the folder scan above is
                    // (issue #397): BuildIndexAsync decodes every image, allocates
                    // a width*height*4 buffer per file, stats files, and runs
                    // SQLite queries that complete synchronously — none of it
                    // yields the UI thread.
                    result = await Task.Run(() => _tagIndexService.BuildIndexAsync(paths, progress, cts.Token));
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                // Runs after a cancelled build too: cancelling stops the loop but
                // keeps every batch already flushed, so the counters and the tag
                // cloud still have to catch up with what did get indexed.
                await RefreshTagIndexStateAsync();

                StatusMessage = cancelled
                    ? "Tag indexing cancelled."
                    : DescribeBuildResult(result);

                // Re-run the filter pipeline. Building the index is the usual way
                // out of "I picked a filter before anything was indexed, so the
                // gallery went empty" — the files that now satisfy that filter
                // only appear if the pipeline runs again. Without this the grid
                // stays empty after a successful build and the only escape is
                // toggling some unrelated filter.
                ApplySortingAndGrouping();
            }, "Indexing images…");
        }
        finally
        {
            IsIndexingTagIndex = false;
            _tagIndexCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Stops a running index build. Whatever batches already flushed stay in
    /// the index, so the run is resumable: the next build skips them.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIndexingTagIndex))]
    private void CancelTagIndex() => _tagIndexCts?.Cancel();

    /// <summary>
    /// Pulls the indexed count, the tag cloud and the per-tile tag data back
    /// into sync after a build. Guarded for the same reason the gallery-load
    /// path is: these are DB calls on a table set that may not exist, and
    /// nothing above <see cref="RunBusyAsync"/> catches.
    /// </summary>
    private async Task RefreshTagIndexStateAsync()
    {
        if (_tagIndexService is null) return;

        try
        {
            IndexedImageCount = await _tagIndexService.GetIndexedCountAsync();
            await RefreshTagCloudAsync();
            await HydrateTagDataAsync(_allMediaItems);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh tag-index state after a build");
        }
    }

    /// <summary>
    /// One-line summary of a finished build for <see cref="StatusMessage"/>.
    /// "Everything failed" gets its own wording because that is what a failed
    /// model download looks like from here — the whole run comes back as
    /// Failed — and a bare "indexed 0 · failed 1,234" reads like a per-file
    /// problem rather than "none of this worked, look at the log".
    /// </summary>
    private static string DescribeBuildResult(TagIndexBuildResult? result)
    {
        if (result is null)
            return "Tag indexing finished.";

        var total = result.Indexed + result.Skipped + result.Failed;
        if (total == 0)
            return "Nothing to index — no images in the enabled gallery folders.";

        if (result.Indexed == 0 && result.Failed == total)
            return $"Tag indexing failed — none of the {total:N0} image(s) could be indexed. Check the log for details.";

        return $"Indexed {result.Indexed:N0} · skipped {result.Skipped:N0} · failed {result.Failed:N0}";
    }

    [RelayCommand]
    private void ToggleTagFilter(string? tagName)
    {
        if (string.IsNullOrEmpty(tagName)) return;

        if (!ActiveTagFilters.Remove(tagName))
            ActiveTagFilters.Add(tagName);

        foreach (var entry in TagCloud)
            entry.IsActive = ActiveTagFilters.Contains(entry.Name);

        OnPropertyChanged(nameof(HasActiveTagFilters));
        ApplySortingAndGrouping();
    }

    [RelayCommand]
    private void ClearTagFilters()
    {
        ActiveTagFilters.Clear();
        foreach (var entry in TagCloud)
            entry.IsActive = false;

        // "Clear filters" has to clear all of them. The NSFW mode filters on
        // its own, so leaving it set would hide the active-filter strip while
        // the gallery stayed filtered. Assigning it also refreshes the
        // Is*-flavored booleans behind the radio buttons (NotifyPropertyChangedFor).
        NsfwFilter = NsfwFilterMode.ShowAll;

        OnPropertyChanged(nameof(HasActiveTagFilters));
        ApplySortingAndGrouping();
    }

    [RelayCommand]
    private void SetNsfwFilter(string mode)
    {
        // The Is*-flavored booleans are notified via [NotifyPropertyChangedFor]
        // on the NsfwFilter backing field, so they stay in sync however
        // NsfwFilter is set (not just through this command).
        NsfwFilter = Enum.Parse<NsfwFilterMode>(mode);
        ApplySortingAndGrouping();
    }

    private async Task RefreshTagCloudAsync()
    {
        if (_tagIndexService is null) return;

        var cloud = await _tagIndexService.GetTagCloudAsync();
        var activeNames = ActiveTagFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TagCloud.ReplaceAll(cloud.Select(t => new TagCloudEntryViewModel(t.Name, t.Count) { IsActive = activeNames.Contains(t.Name) }).ToList());
        OnPropertyChanged(nameof(TagCloudHeader));
    }

    private async Task HydrateTagDataAsync(IReadOnlyList<GenerationGalleryMediaItemViewModel> items)
    {
        if (_tagIndexService is null) return;

        var paths = items.Where(i => i.IsImage).Select(i => i.FilePath).ToList();
        if (paths.Count == 0) return;

        var lookup = await _tagIndexService.GetTagsForFilesAsync(paths);
        foreach (var item in items)
        {
            if (lookup.TryGetValue(Path.GetFullPath(item.FilePath), out var info))
            {
                item.IsNsfw = info.IsNsfw;
                item.Tags = info.Tags;
            }
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

        // Recorded before the pipeline starts: ApplySortedResults recomputes
        // the empty-state message on every run and needs this to distinguish
        // "no folders configured" from "folders configured but empty".
        _enabledSourceCount = enabledSourceCount;

        ApplySortingAndGrouping();

        // The pipeline sets this again when it completes; doing it here too
        // keeps a load that never reaches ApplySortedResults (a faulted filter
        // pass) from leaving the empty state with no text at all.
        UpdateNoMediaMessage();

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        _lastClickedItem = null;
        UpdateSelectionState();
    }

    /// <summary>
    /// Picks the empty-state text for the current situation. An active
    /// tag/NSFW filter takes priority: an empty grid then means "nothing
    /// matched", which needs a completely different next step from the user
    /// than "no folders configured". Notably, a HideNsfw/NsfwOnly filter only
    /// matches files that are already in the tag index, so a gallery that has
    /// never been indexed empties out entirely — without this message that
    /// looks like the gallery itself broke.
    /// </summary>
    private void UpdateNoMediaMessage()
    {
        if (HasActiveTagFilters && MediaItems.Count == 0)
        {
            NoMediaMessage = "No images match your current filters. Try clearing the filters, or build the tag index if you haven't done that yet.";
            return;
        }

        NoMediaMessage = _enabledSourceCount == 0
            ? "No generation gallery folders are enabled. Configure Generation Galleries in Settings to get started."
            : "No media found in enabled generation gallery folders. Check your Generation Galleries in Settings.";
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

        // Tag/NSFW filtering needs the database, so it's resolved here (async,
        // before the CPU-bound Task.Run below) rather than inside the closure.
        // Skipped entirely when no tag filter is active, so the common case
        // (typing in the filename search box) never touches the DB.
        HashSet<string>? tagMatchPaths = null;
        if (HasActiveTagFilters && _tagIndexService is not null)
        {
            try
            {
                var matches = await _tagIndexService.SearchAsync(ActiveTagFilters.ToList(), NsfwFilter);
                tagMatchPaths = matches.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // A tag filter that cannot be resolved degrades to "no tag
                // filter" rather than taking the gallery with it: this runs
                // from property setters and fire-and-forget continuations, so
                // an escape lands as an unhandled dispatcher exception.
                Logger.Warning(ex, "Tag search failed; the gallery is showing results without the tag filter applied");
                tagMatchPaths = null;
            }
        }

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

            if (tagMatchPaths is not null)
            {
                filtered = filtered.Where(item => tagMatchPaths.Contains(Path.GetFullPath(item.FilePath)));
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

        FilteredMatchCount = sortedList.Count;

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

        UpdateNoMediaMessage();

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

    /// <summary>
    /// The single exit from the gallery for a media item — both delete
    /// commands and <see cref="OnActionsFilesMoved"/> route through here, so
    /// it is also where the tag index learns the file is gone.
    /// </summary>
    private void RemoveMediaItem(GenerationGalleryMediaItemViewModel item)
    {
        _allMediaItems.Remove(item);
        MediaItems.Remove(item);
        VisibleMediaItems.Remove(item);
        UpdateGroupedMediaItems(MediaItems.ToList());
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        OnPropertyChanged(nameof(HasMoreItems));

        if (!item.IsImage) return;

        // The "N / M indexed" pill counts images, so M has to shrink with them
        // — otherwise every delete leaves the counter permanently overstated.
        TotalGalleryImageCount = Math.Max(0, TotalGalleryImageCount - 1);

        if (_tagIndexService is null) return;

        // Fire-and-forget: pruning is best-effort consistency cleanup, not
        // something a delete should block on. Chained onto the previous prune
        // so a bulk delete issues them one at a time instead of opening N DB
        // contexts at once.
        _lastTagIndexPruneTask = PruneTagIndexEntryAsync(item.FilePath, _lastTagIndexPruneTask);
    }

    /// <summary>
    /// Drops the index row for a file that just left the gallery and corrects
    /// <see cref="IndexedImageCount"/> by however many rows actually went —
    /// zero for a file that was never indexed, so the counter stays honest
    /// without a second query to find out.
    /// </summary>
    private async Task PruneTagIndexEntryAsync(string filePath, Task previous)
    {
        try
        {
            await previous;
        }
        catch
        {
            // Defensive: this method swallows its own failures, so `previous`
            // should never be faulted. Catching anyway keeps one bad link from
            // poisoning every prune that chains onto it afterwards.
        }

        if (_tagIndexService is null) return;

        try
        {
            var removed = await _tagIndexService.RemoveIndexEntriesAsync([filePath]);
            if (removed > 0)
            {
                IndexedImageCount = Math.Max(0, IndexedImageCount - removed);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to prune the tag index entry for {Path}", filePath);
        }
    }

    /// <summary>
    /// Waits for the index-prune calls started by <see cref="RemoveMediaItem"/>.
    /// Intended for test support.
    /// </summary>
    public Task WaitForTagIndexPruneAsync() => _lastTagIndexPruneTask;

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
