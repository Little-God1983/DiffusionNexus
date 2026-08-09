# Gallery Tag Index & Advanced Search UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the tag index engine into the Generation Gallery screen exactly as approved in the mockup: a **Build Tag Index** toolbar action, an **Advanced Search** drawer with a clickable frequency-weighted tag cloud and a content-rating filter, an active-filter strip above the grid, and an NSFW badge + hover tag-chips on each tile.

**Architecture:** `GenerationGalleryViewModel` gains an optional `ITagIndexService?` dependency (same nullable-trailing-parameter convention as its existing `IImageFavoritesService?`). Tag/NSFW data for on-screen tiles is bulk-hydrated after every gallery load via one new lookup method (`GetTagsForFilesAsync`) added to `ITagIndexService`, so displaying badges never costs one DB round trip per tile. Tag-cloud and multi-tag search filtering compose with the *existing* date/filename/favorites filter pipeline in `ApplySortingAndGroupingAsync` as one more `.Where(...)` clause — they don't replace it.

**Tech Stack:** Avalonia (XAML + code-behind), CommunityToolkit.Mvvm, existing `BatchObservableCollection<T>` pattern, xUnit + FluentAssertions + Moq.

## Global Constraints

- Depends on `2026-08-10-image-tagging-engine-and-index.md` (all tasks) — `ITagIndexService`/`TagIndexService` must exist and be DI-registered before starting Task 1 here.
- `_tagIndexService` is nullable everywhere it's threaded (constructor param, fields) — every new command/behavior no-ops gracefully when it's null, matching how `_favoritesService`/`_videoThumbnailService` are already handled in this file. The design-time constructor (no services) must keep working unchanged.
- New collections use `BatchObservableCollection<T>` and `.ReplaceAll(...)`, matching `MediaItems`/`VisibleMediaItems`/`GroupedMediaItems` — not plain `ObservableCollection<T>` with manual `Clear()`/`Add()` loops.
- Tag-cloud/NSFW filtering only queries the database when at least one tag filter or a non-default NSFW mode is active — typing in the existing filename search box or changing date/sort must not trigger a DB round trip when no tag filter is engaged.
- Follow the visual language already locked in the approved mockup: violet (`#b388ff` family) marks every new/tag-related control; NSFW badges use amber (`#ffa726`), not the existing delete-red or favorite-gold.

---

## File Structure

- **Modify** `DiffusionNexus.Domain/Services/ITagIndexService.cs` — add `GetTagsForFilesAsync`.
- **Modify** `DiffusionNexus.Service/Services/TagIndexService.cs` — implement it.
- **Modify** `DiffusionNexus.Tests/Service/Services/TagIndexServiceTests.cs` — cover it.
- **Modify** `DiffusionNexus.UI/ViewModels/GenerationGalleryMediaItemViewModel.cs` — add `IsNsfw`, `Tags`, `TopTags`.
- **Create** `DiffusionNexus.UI/ViewModels/TagCloudEntryViewModel.cs`.
- **Modify** `DiffusionNexus.UI/ViewModels/GenerationGalleryViewModel.cs` — constructor param, index-status state, Advanced Search state/commands, filter-pipeline integration.
- **Modify** `DiffusionNexus.UI/Views/GenerationGalleryView.axaml` — toolbar buttons, active-filter strip row, Advanced Search drawer, tile template additions.
- **Modify** `DiffusionNexus.UI/App.axaml.cs` — thread `ITagIndexService` into the `GenerationGalleryViewModel` DI factory.
- **Modify** `DiffusionNexus.Tests/Viewer/GenerationGalleryViewModelTests.cs` — cover the new commands/filtering.

---

### Task 1: `ITagIndexService.GetTagsForFilesAsync` (bulk tile hydration)

**Files:**
- Modify: `DiffusionNexus.Domain/Services/ITagIndexService.cs`
- Modify: `DiffusionNexus.Service/Services/TagIndexService.cs`
- Modify: `DiffusionNexus.Tests/Service/Services/TagIndexServiceTests.cs`

**Interfaces:**
- Produces: `ITagIndexService.GetTagsForFilesAsync(IReadOnlyList<string>, CancellationToken) → Task<IReadOnlyDictionary<string, ImageTagLookup>>`, `ImageTagLookup(bool IsNsfw, IReadOnlyList<string> Tags)`. Consumed by Task 4.

- [ ] **Step 1: Write the failing test**

Add to `DiffusionNexus.Tests/Service/Services/TagIndexServiceTests.cs`:

```csharp
    [Fact]
    public async Task GetTagsForFilesAsync_ReturnsNsfwFlagAndTagsKeyedByNormalizedPath()
    {
        var path = CreateFakeImage("lookup.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(
                new[] { new ImageTagScore("dog", 0.9f), new ImageTagScore("outdoor", 0.6f) },
                "explicit", 0.7f, isNsfw: true));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { path });

        var lookup = await service.GetTagsForFilesAsync(new[] { path });

        var entry = lookup[Path.GetFullPath(path)];
        entry.IsNsfw.Should().BeTrue();
        entry.Tags.Should().BeEquivalentTo(new[] { "dog", "outdoor" });
    }

    [Fact]
    public async Task GetTagsForFilesAsync_OmitsUnindexedPaths()
    {
        var service = new TagIndexService(new SingleDbContextFactory(_options), new Mock<IImageTaggingService>().Object);

        var lookup = await service.GetTagsForFilesAsync(new[] { @"C:\never\indexed.png" });

        lookup.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TagIndexServiceTests"`
Expected: FAIL to compile — `GetTagsForFilesAsync`/`ImageTagLookup` don't exist yet.

- [ ] **Step 3: Extend the contract**

In `DiffusionNexus.Domain/Services/ITagIndexService.cs`, add alongside the other record types:

```csharp
public sealed record ImageTagLookup(bool IsNsfw, IReadOnlyList<string> Tags);
```

and add to the interface:

```csharp
    /// <summary>
    /// Bulk lookup for gallery tile hydration: NSFW flag + tag names for
    /// every already-indexed path in <paramref name="filePaths"/>. Paths with
    /// no index row (never indexed, or since deleted) are simply absent from
    /// the result — callers should treat a missing key as "not yet tagged."
    /// </summary>
    Task<IReadOnlyDictionary<string, ImageTagLookup>> GetTagsForFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement it**

In `DiffusionNexus.Service/Services/TagIndexService.cs`, add:

```csharp
    public async Task<IReadOnlyDictionary<string, ImageTagLookup>> GetTagsForFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0)
            return new Dictionary<string, ImageTagLookup>();

        var normalizedPaths = filePaths.Select(Path.GetFullPath).ToList();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await context.ImageMediaTagIndexes
            .Where(e => normalizedPaths.Contains(e.FilePath))
            .Include(e => e.TagAssignments).ThenInclude(a => a.ImageTag)
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            e => e.FilePath,
            e => new ImageTagLookup(e.IsNsfw, e.TagAssignments.Select(a => a.ImageTag!.Name).ToList()),
            StringComparer.OrdinalIgnoreCase);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TagIndexServiceTests"`
Expected: PASS (7 tests total in this file)

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Domain/Services/ITagIndexService.cs DiffusionNexus.Service/Services/TagIndexService.cs DiffusionNexus.Tests/Service/Services/TagIndexServiceTests.cs
git commit -m "feat: add bulk tag lookup for gallery tile hydration"
```

---

### Task 2: Tile-level NSFW/tag data on `GenerationGalleryMediaItemViewModel`

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/GenerationGalleryMediaItemViewModel.cs`

**Interfaces:**
- Produces: `GenerationGalleryMediaItemViewModel.IsNsfw` (settable `bool`), `.Tags` (settable `IReadOnlyList<string>`), `.TopTags` (computed, top 3). Consumed by Task 4 (hydration) and Task 5 (XAML binding).

- [ ] **Step 1: Add the properties**

In `DiffusionNexus.UI/ViewModels/GenerationGalleryMediaItemViewModel.cs`, add fields next to `_isFavorite`:

```csharp
    private bool _isNsfw;
    private IReadOnlyList<string> _tags = Array.Empty<string>();
```

Add properties next to `IsFavorite` (after its closing brace, before `Thumbnail`):

```csharp
    /// <summary>Whether the tag index flagged this image as NSFW. False (default) until indexed.</summary>
    public bool IsNsfw
    {
        get => _isNsfw;
        set => SetProperty(ref _isNsfw, value);
    }

    /// <summary>Content tags from the tag index. Empty until indexed.</summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set
        {
            if (SetProperty(ref _tags, value))
            {
                OnPropertyChanged(nameof(TopTags));
            }
        }
    }

    /// <summary>The first 3 tags, for the tile's hover strip — keeps the card readable.</summary>
    public IReadOnlyList<string> TopTags => _tags.Count <= 3 ? _tags : _tags.Take(3).ToList();
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: succeeds — this class has no existing tests to break (verified: no `GenerationGalleryMediaItemViewModelTests.cs` exists in the repo).

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/GenerationGalleryMediaItemViewModel.cs
git commit -m "feat: add IsNsfw/Tags to gallery media item view model"
```

---

### Task 3: `TagCloudEntryViewModel`

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/TagCloudEntryViewModel.cs`

**Interfaces:**
- Produces: `TagCloudEntryViewModel(string name, int count)` with `Name`, `Count`, `DisplayText`, settable `IsActive`. Consumed by Task 4 and Task 5.

- [ ] **Step 1: Create the file**

```csharp
// DiffusionNexus.UI/ViewModels/TagCloudEntryViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One clickable chip in the Advanced Search tag cloud.</summary>
public partial class TagCloudEntryViewModel : ObservableObject
{
    public string Name { get; }
    public int Count { get; }
    public string DisplayText => $"{Name} ({Count})";

    [ObservableProperty]
    private bool _isActive;

    public TagCloudEntryViewModel(string name, int count)
    {
        Name = name;
        Count = count;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/TagCloudEntryViewModel.cs
git commit -m "feat: add TagCloudEntryViewModel"
```

---

### Task 4: Wire `GenerationGalleryViewModel`

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/GenerationGalleryViewModel.cs`
- Modify: `DiffusionNexus.Tests/Viewer/GenerationGalleryViewModelTests.cs`

**Interfaces:**
- Consumes: `ITagIndexService` (Task 1 of the engine plan + Task 1 here), `TagCloudEntryViewModel` (Task 3), `GenerationGalleryMediaItemViewModel.IsNsfw/Tags` (Task 2).
- Produces: `BuildTagIndexCommand`, `ToggleAdvancedSearchCommand`, `ToggleTagFilterCommand`, `ClearTagFiltersCommand`, `SetNsfwFilterCommand`, `IndexStatusText`, `IsAdvancedSearchOpen`, `TagCloud`, `ActiveTagFilters`, `HasActiveTagFilters`, `FilteredMatchCountText`, `TagCloudHeader`, `IsNsfwFilterShowAll/HideNsfw/NsfwOnly`. Consumed by Task 5 (XAML) and Task 6 (DI).

- [ ] **Step 1: Write the failing tests**

Add to `DiffusionNexus.Tests/Viewer/GenerationGalleryViewModelTests.cs` (mirror the existing constructor-call pattern already used by every test in this file, adding the new `ITagIndexService` mock as the 6th positional/named argument):

```csharp
    [Fact]
    public async Task BuildTagIndexCommand_UpdatesIndexedCount_AndPopulatesTagCloud()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.BuildIndexAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIndexBuildResult(Indexed: 2, Skipped: 0, Failed: 0, NsfwCount: 0));
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        mockTagIndex.Setup(t => t.GetTagCloudAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TagFrequency("dog", 2) });
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null,
            tagIndexService: mockTagIndex.Object);

        await viewModel.BuildTagIndexCommand.ExecuteAsync(null);

        viewModel.IndexedImageCount.Should().Be(2);
        viewModel.TagCloud.Should().ContainSingle(t => t.Name == "dog" && t.Count == 2);
    }

    [Fact]
    public void ToggleAdvancedSearchCommand_TogglesIsAdvancedSearchOpen()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        viewModel.IsAdvancedSearchOpen.Should().BeFalse();
        viewModel.ToggleAdvancedSearchCommand.Execute(null);
        viewModel.IsAdvancedSearchOpen.Should().BeTrue();
        viewModel.ToggleAdvancedSearchCommand.Execute(null);
        viewModel.IsAdvancedSearchOpen.Should().BeFalse();
    }

    [Fact]
    public void ToggleTagFilterCommand_AddsThenRemovesTag_AndTracksHasActiveTagFilters()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        viewModel.HasActiveTagFilters.Should().BeFalse();
        viewModel.ToggleTagFilterCommand.Execute("dog");
        viewModel.ActiveTagFilters.Should().Contain("dog");
        viewModel.HasActiveTagFilters.Should().BeTrue();

        viewModel.ToggleTagFilterCommand.Execute("dog");
        viewModel.ActiveTagFilters.Should().NotContain("dog");
        viewModel.HasActiveTagFilters.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySortingAndGrouping_WithActiveTagFilter_RestrictsToSearchAsyncResults()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.GetSettingsAsync()).ReturnsAsync(new AppSettings
        {
            ImageGalleries = { new ImageGallery { FolderPath = Path.GetTempPath(), IsEnabled = true } }
        });
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.SearchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<NsfwFilterMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>()); // no file matches "dog" in this fixture

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null,
            tagIndexService: mockTagIndex.Object);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.ToggleTagFilterCommand.Execute("dog");
        await viewModel.WaitForSortingAsync();

        viewModel.MediaItems.Should().BeEmpty();
        mockTagIndex.Verify(t => t.SearchAsync(
            It.Is<IReadOnlyList<string>>(tags => tags.Contains("dog")),
            NsfwFilterMode.ShowAll,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~GenerationGalleryViewModelTests"`
Expected: FAIL to compile — no `tagIndexService` parameter, no new commands/properties yet.

- [ ] **Step 3: Add the constructor parameter and field**

Replace the second constructor's signature and body opening in `DiffusionNexus.UI/ViewModels/GenerationGalleryViewModel.cs`:

```csharp
    public GenerationGalleryViewModel(
        IAppSettingsService settingsService,
        IDatasetEventAggregator eventAggregator,
        IDatasetState datasetState,
        IVideoThumbnailService? videoThumbnailService,
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        IImageFavoritesService? favoritesService = null)
```

with:

```csharp
    public GenerationGalleryViewModel(
        IAppSettingsService settingsService,
        IDatasetEventAggregator eventAggregator,
        IDatasetState datasetState,
        IVideoThumbnailService? videoThumbnailService,
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        IImageFavoritesService? favoritesService = null,
        ITagIndexService? tagIndexService = null)
```

and add `_tagIndexService = tagIndexService;` next to the existing `_favoritesService = favoritesService;` assignment. Add the field next to `_favoritesService`:

```csharp
    private readonly ITagIndexService? _tagIndexService;
```

- [ ] **Step 4: Add index-status and Advanced Search state**

Add near the other `[ObservableProperty]` declarations (after `_showFavoritesOnly`):

```csharp
    [ObservableProperty]
    private int _indexedImageCount;

    [ObservableProperty]
    private int _totalGalleryImageCount;

    [ObservableProperty]
    private bool _isAdvancedSearchOpen;

    [ObservableProperty]
    private NsfwFilterMode _nsfwFilter = NsfwFilterMode.ShowAll;

    [ObservableProperty]
    private int _filteredMatchCount;
```

Add near the other computed properties (after `IsGroupingEnabled`):

```csharp
    public string IndexStatusText => $"{IndexedImageCount:N0} / {TotalGalleryImageCount:N0} indexed";

    public BatchObservableCollection<TagCloudEntryViewModel> TagCloud { get; } = [];

    public ObservableCollection<string> ActiveTagFilters { get; } = [];

    public bool HasActiveTagFilters => ActiveTagFilters.Count > 0;

    public bool IsNsfwFilterShowAll => NsfwFilter == NsfwFilterMode.ShowAll;
    public bool IsNsfwFilterHideNsfw => NsfwFilter == NsfwFilterMode.HideNsfw;
    public bool IsNsfwFilterNsfwOnly => NsfwFilter == NsfwFilterMode.NsfwOnly;

    public string FilteredMatchCountText => $"{FilteredMatchCount:N0} images match";

    public string TagCloudHeader => $"TAG INDEX — {TotalGalleryImageCount:N0} images · {TagCloud.Count:N0} tags";
```

- [ ] **Step 5: Add the commands and hydration helpers**

Add near the other `[RelayCommand]` methods (after `ToggleSelectedFavoritesAsync`):

```csharp
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

        await RunBusyAsync(async () =>
        {
            var paths = _allMediaItems.Where(i => i.IsImage).Select(i => i.FilePath).ToList();
            var progress = new Progress<TagIndexBuildProgress>(p =>
            {
                BusyMessage = p.CurrentFile is not null
                    ? $"Indexing images… {p.Completed}/{p.Total}"
                    : "Finalizing index…";
            });

            await _tagIndexService.BuildIndexAsync(paths, progress);
            IndexedImageCount = await _tagIndexService.GetIndexedCountAsync();
            await RefreshTagCloudAsync();
            await HydrateTagDataAsync(_allMediaItems);
        }, "Indexing images…");
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

        OnPropertyChanged(nameof(HasActiveTagFilters));
        ApplySortingAndGrouping();
    }

    [RelayCommand]
    private void SetNsfwFilter(string mode)
    {
        NsfwFilter = Enum.Parse<NsfwFilterMode>(mode);
        OnPropertyChanged(nameof(IsNsfwFilterShowAll));
        OnPropertyChanged(nameof(IsNsfwFilterHideNsfw));
        OnPropertyChanged(nameof(IsNsfwFilterNsfwOnly));
        ApplySortingAndGrouping();
    }

    private async Task RefreshTagCloudAsync()
    {
        if (_tagIndexService is null) return;

        var cloud = await _tagIndexService.GetTagCloudAsync();
        var activeNames = ActiveTagFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TagCloud.ReplaceAll(cloud.Select(t => new TagCloudEntryViewModel(t.Name, t.Count) { IsActive = activeNames.Contains(t.Name) }));
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
```

- [ ] **Step 6: Hook into `LoadMediaAsync`**

Replace:

```csharp
            var mediaItems = await Task.Run(() => CollectMediaItemsAsync(enabledPaths, includeSubFolders));
            await ApplyMediaItemsAsync(mediaItems, enabledPaths.Count);

            // Fire-and-forget: generate missing video thumbnails after gallery is displayed
            StartBackgroundVideoThumbnailGeneration(mediaItems);
```

with:

```csharp
            var mediaItems = await Task.Run(() => CollectMediaItemsAsync(enabledPaths, includeSubFolders));
            await ApplyMediaItemsAsync(mediaItems, enabledPaths.Count);

            TotalGalleryImageCount = mediaItems.Count(i => i.IsImage);
            if (_tagIndexService is not null)
            {
                IndexedImageCount = await _tagIndexService.GetIndexedCountAsync();
                await HydrateTagDataAsync(mediaItems);
            }

            // Fire-and-forget: generate missing video thumbnails after gallery is displayed
            StartBackgroundVideoThumbnailGeneration(mediaItems);
```

- [ ] **Step 7: Integrate tag/NSFW filtering into the existing filter pipeline**

Replace the start of `ApplySortingAndGroupingAsync` (the variable-capture block, before the `Task.Run`):

```csharp
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
```

with:

```csharp
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
        var hasTagFilter = ActiveTagFilters.Count > 0 || NsfwFilter != NsfwFilterMode.ShowAll;
        if (hasTagFilter && _tagIndexService is not null)
        {
            var matches = await _tagIndexService.SearchAsync(ActiveTagFilters.ToList(), NsfwFilter);
            tagMatchPaths = matches.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
```

(The rest of the method — sort, group creation, `resultList`/`resultGroups` — is unchanged.)

- [ ] **Step 8: Record the match count**

Replace:

```csharp
        // Back on the original context (UI thread) — apply results directly
        ApplySortedResults(sortedList, groups);
    }
```

with:

```csharp
        FilteredMatchCount = sortedList.Count;

        // Back on the original context (UI thread) — apply results directly
        ApplySortedResults(sortedList, groups);
    }
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~GenerationGalleryViewModelTests"`
Expected: PASS, including the 4 new tests and every pre-existing test in this file (the new constructor parameter is optional and trailing).

- [ ] **Step 10: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/GenerationGalleryViewModel.cs DiffusionNexus.Tests/Viewer/GenerationGalleryViewModelTests.cs
git commit -m "feat: wire tag index build/search into GenerationGalleryViewModel"
```

---

### Task 5: XAML — toolbar, filter strip, Advanced Search drawer, tile badges

**Files:**
- Modify: `DiffusionNexus.UI/Views/GenerationGalleryView.axaml`

**Interfaces:**
- Consumes: everything produced by Task 4, plus `GenerationGalleryMediaItemViewModel.IsNsfw/TopTags` (Task 2) and `TagCloudEntryViewModel.DisplayText/IsActive` (Task 3).

- [ ] **Step 1: Add chip styles**

Add a `<UserControl.Styles>` block right after the existing `<UserControl.Resources>` block closes (before `<Grid RowDefinitions=...>`):

```xml
  <UserControl.Styles>
    <Style Selector="Button.tagchip">
      <Setter Property="Background" Value="#202020"/>
      <Setter Property="Foreground" Value="#9a9a9a"/>
      <Setter Property="BorderBrush" Value="#3a3a3a"/>
      <Setter Property="BorderThickness" Value="1"/>
      <Setter Property="CornerRadius" Value="100"/>
      <Setter Property="Padding" Value="10,5"/>
      <Setter Property="Margin" Value="0,0,6,6"/>
      <Setter Property="FontSize" Value="12"/>
    </Style>
    <Style Selector="Button.tagchip.active">
      <Setter Property="Background" Value="#b388ff"/>
      <Setter Property="Foreground" Value="#1a1030"/>
      <Setter Property="BorderBrush" Value="#b388ff"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
    <Style Selector="Border.mediaCard:pointerover #TagStripHost">
      <Setter Property="Opacity" Value="1"/>
    </Style>
  </UserControl.Styles>
```

- [ ] **Step 2: Add the two toolbar buttons**

In the top toolbar `WrapPanel`, replace:

```xml
        <Button Content="Open Folder"
                Command="{Binding OpenFolderInExplorerCommand}"
                Padding="10,6" Margin="0,4,8,4"
                IsEnabled="{Binding HasSelection}"
                ToolTip.Tip="Open the selected image's folder in Explorer"/>
```

with:

```xml
        <Button Content="🏷️ Build Tag Index"
                Command="{Binding BuildTagIndexCommand}"
                Padding="10,6" Margin="0,4,4,4"
                ToolTip.Tip="Scan enabled gallery folders and tag new/changed images"/>
        <Border Background="#1c1c1c" BorderBrush="#3a3a3a" BorderThickness="1" CornerRadius="100"
                Padding="8,2" Margin="0,4,12,4" VerticalAlignment="Center">
          <TextBlock Text="{Binding IndexStatusText}" FontSize="11" Foreground="#9a9a9a"/>
        </Border>

        <Button Content="🔎 Advanced Search"
                Command="{Binding ToggleAdvancedSearchCommand}"
                Padding="10,6" Margin="0,4,12,4"
                ToolTip.Tip="Filter the gallery by tag and content rating"/>

        <Button Content="Open Folder"
                Command="{Binding OpenFolderInExplorerCommand}"
                Padding="10,6" Margin="0,4,8,4"
                IsEnabled="{Binding HasSelection}"
                ToolTip.Tip="Open the selected image's folder in Explorer"/>
```

- [ ] **Step 3: Add a row for the active-filter strip**

Replace:

```xml
  <Grid RowDefinitions="Auto,Auto,*" Background="#252526">
```

with:

```xml
  <Grid RowDefinitions="Auto,Auto,Auto,*" Background="#252526">
```

Replace `<Grid Grid.Row="2">` (the content grid containing the busy overlay/empty-state/ScrollViewer) with `<Grid Grid.Row="3">`, and insert this new row directly before it (after the selection-toolbar `Border Grid.Row="1"` closes):

```xml
    <Border Grid.Row="2" Background="#1E1E1E" Padding="12,8" BorderBrush="#333" BorderThickness="0,0,0,1"
            IsVisible="{Binding HasActiveTagFilters}">
      <WrapPanel Orientation="Horizontal">
        <TextBlock Text="Filtered by:" Opacity="0.7" VerticalAlignment="Center" Margin="0,4,8,4"/>
        <ItemsControl ItemsSource="{Binding ActiveTagFilters}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><WrapPanel Orientation="Horizontal"/></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="x:String">
              <Border Background="#2a2140" BorderBrush="#6a4fb3" BorderThickness="1" CornerRadius="100"
                      Padding="10,4" Margin="0,4,8,4">
                <StackPanel Orientation="Horizontal" Spacing="6">
                  <TextBlock Text="{Binding}" FontSize="12" Foreground="#d9c7ff"/>
                  <Button Content="✕" FontSize="10" Padding="0" Background="Transparent" BorderThickness="0"
                          Foreground="#d9c7ff"
                          Command="{Binding $parent[ItemsControl].((vm:GenerationGalleryViewModel)DataContext).ToggleTagFilterCommand}"
                          CommandParameter="{Binding}"/>
                </StackPanel>
              </Border>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <Button Content="Clear filters" Command="{Binding ClearTagFiltersCommand}"
                Background="Transparent" BorderThickness="0" Foreground="#9a9a9a"
                Padding="4" Margin="0,4,8,4"/>
      </WrapPanel>
    </Border>
```

- [ ] **Step 4: Add the Advanced Search drawer**

Inside the (now `Grid.Row="3"`) content `Grid`, add these two children right after the closing `</ScrollViewer>` tag (still inside the outer `<Grid Grid.Row="3">`):

```xml
      <Border IsVisible="{Binding IsAdvancedSearchOpen}"
              Background="#66000000" ZIndex="90"
              Tapped="OnAdvancedSearchBackdropTapped"/>

      <Border IsVisible="{Binding IsAdvancedSearchOpen}"
              Width="340" HorizontalAlignment="Right"
              Background="#1a1a1c" BorderBrush="#6a4fb3" BorderThickness="1,0,0,0"
              ZIndex="95">
        <Grid RowDefinitions="Auto,*,Auto">
          <Border Grid.Row="0" Padding="16,14" BorderBrush="#333" BorderThickness="0,0,0,1">
            <Grid ColumnDefinitions="*,Auto">
              <TextBlock Grid.Column="0" Text="Advanced Search" FontSize="14" FontWeight="SemiBold" VerticalAlignment="Center"/>
              <Button Grid.Column="1" Content="✕" Command="{Binding ToggleAdvancedSearchCommand}"
                      Background="Transparent" BorderThickness="0" Foreground="#9a9a9a"/>
            </Grid>
          </Border>

          <ScrollViewer Grid.Row="1" Padding="16">
            <StackPanel Spacing="18">
              <StackPanel Spacing="8">
                <TextBlock Text="CONTENT RATING" FontSize="11" Foreground="#6f6f6f" FontWeight="SemiBold"/>
                <StackPanel Orientation="Horizontal" Spacing="4">
                  <RadioButton Content="Show all" GroupName="NsfwFilter"
                               IsChecked="{Binding IsNsfwFilterShowAll}"
                               Command="{Binding SetNsfwFilterCommand}" CommandParameter="ShowAll"/>
                  <RadioButton Content="Hide NSFW" GroupName="NsfwFilter"
                               IsChecked="{Binding IsNsfwFilterHideNsfw}"
                               Command="{Binding SetNsfwFilterCommand}" CommandParameter="HideNsfw"/>
                  <RadioButton Content="NSFW only" GroupName="NsfwFilter"
                               IsChecked="{Binding IsNsfwFilterNsfwOnly}"
                               Command="{Binding SetNsfwFilterCommand}" CommandParameter="NsfwOnly"/>
                </StackPanel>
              </StackPanel>

              <StackPanel Spacing="8">
                <TextBlock Text="{Binding TagCloudHeader}" FontSize="11" Foreground="#6f6f6f" FontWeight="SemiBold"/>
                <ItemsControl ItemsSource="{Binding TagCloud}">
                  <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel Orientation="Horizontal"/></ItemsPanelTemplate>
                  </ItemsControl.ItemsPanel>
                  <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="vm:TagCloudEntryViewModel">
                      <Button Classes="tagchip" Classes.active="{Binding IsActive}"
                              Content="{Binding DisplayText}"
                              Command="{Binding $parent[ItemsControl].((vm:GenerationGalleryViewModel)DataContext).ToggleTagFilterCommand}"
                              CommandParameter="{Binding Name}"/>
                    </DataTemplate>
                  </ItemsControl.ItemTemplate>
                </ItemsControl>
              </StackPanel>
            </StackPanel>
          </ScrollViewer>

          <Border Grid.Row="2" Padding="16" BorderBrush="#333" BorderThickness="0,1,0,0">
            <TextBlock Text="{Binding FilteredMatchCountText}" FontSize="12" Foreground="#9a9a9a" HorizontalAlignment="Center"/>
          </Border>
        </Grid>
      </Border>
```

- [ ] **Step 5: Add the backdrop click-to-close handler**

In `DiffusionNexus.UI/Views/GenerationGalleryView.axaml.cs`, add:

```csharp
    private void OnAdvancedSearchBackdropTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is GenerationGalleryViewModel vm)
        {
            vm.ToggleAdvancedSearchCommand.Execute(null);
        }
    }
```

(Check the file's existing `using` directives first — `Avalonia.Input` may already be imported, in which case drop the fully-qualified prefix to match the file's style.)

- [ ] **Step 6: Add NSFW badge and tag-chip hover strip to the tile template**

In `MediaItemTemplate`, add `Classes="mediaCard"` to the outer `Border`:

```xml
    <DataTemplate x:Key="MediaItemTemplate" x:DataType="vm:GenerationGalleryMediaItemViewModel">
      <Border Classes="mediaCard"
              Height="{Binding $parent[ScrollViewer].((vm:GenerationGalleryViewModel)DataContext).TileHeight}"
```

Replace the "File Extension Label (Top Left)" block:

```xml
          <!-- File Extension Label (Top Left) -->
          <Border Background="#66000000" CornerRadius="4" Padding="6,2"
                  HorizontalAlignment="Left" VerticalAlignment="Top" Margin="6" ZIndex="5">
            <TextBlock Text="{Binding FileExtension}" Foreground="White" FontSize="11" FontWeight="SemiBold"/>
          </Border>
```

with:

```xml
          <!-- File Extension Label + NSFW Badge (Top Left) -->
          <StackPanel Orientation="Horizontal" Spacing="4"
                      HorizontalAlignment="Left" VerticalAlignment="Top" Margin="6" ZIndex="5">
            <Border Background="#33ffa726" BorderBrush="#ffa726" BorderThickness="1" CornerRadius="4"
                    Padding="6,2" IsVisible="{Binding IsNsfw}">
              <TextBlock Text="NSFW" Foreground="#ffa726" FontSize="10" FontWeight="Bold"/>
            </Border>
            <Border Background="#66000000" CornerRadius="4" Padding="6,2">
              <TextBlock Text="{Binding FileExtension}" Foreground="White" FontSize="11" FontWeight="SemiBold"/>
            </Border>
          </StackPanel>
```

Add the hover tag strip right before the existing `<Border Background="#CC1A1A1A" VerticalAlignment="Bottom" ...>` filename bar:

```xml
          <Border Name="TagStripHost" VerticalAlignment="Bottom" Margin="0,0,0,27" ZIndex="4"
                  Opacity="0" IsHitTestVisible="False">
            <ItemsControl ItemsSource="{Binding TopTags}" Margin="8,0,8,4">
              <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate><WrapPanel Orientation="Horizontal"/></ItemsPanelTemplate>
              </ItemsControl.ItemsPanel>
              <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="x:String">
                  <Border Background="#38b388ff" BorderBrush="#66b388ff" BorderThickness="1" CornerRadius="100"
                          Padding="6,2" Margin="0,0,4,0">
                    <TextBlock Text="{Binding}" FontSize="9" Foreground="#e4d8ff"/>
                  </Border>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </Border>
```

- [ ] **Step 7: Build**

Run: `dotnet build`
Expected: succeeds. AXAML binding errors surface at runtime, not build time — Task 7's manual smoke test is where binding-path typos actually get caught.

- [ ] **Step 8: Commit**

```bash
git add DiffusionNexus.UI/Views/GenerationGalleryView.axaml DiffusionNexus.UI/Views/GenerationGalleryView.axaml.cs
git commit -m "feat: add Build Tag Index / Advanced Search UI to the gallery view"
```

---

### Task 6: DI wiring

**Files:**
- Modify: `DiffusionNexus.UI/App.axaml.cs`

- [ ] **Step 1: Pass `ITagIndexService` into the `GenerationGalleryViewModel` factory**

At the factory registered around line 916 (`services.AddScoped<GenerationGalleryViewModel>(sp => new GenerationGalleryViewModel(...))`), replace:

```csharp
            sp.GetService<IImageFavoritesService>()));
```

(the last line of that specific factory call — confirm by context, since `IImageFavoritesService` also appears in other factories in this file) with:

```csharp
            sp.GetService<IImageFavoritesService>(),
            sp.GetService<ITagIndexService>()));
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/App.axaml.cs
git commit -m "feat: register ITagIndexService with GenerationGalleryViewModel"
```

---

### Task 7: Manual GUI smoke test

Per this repo's own guidance for UI changes: type-checking and the test suite verify code correctness, not feature correctness. Actually run the app.

**Files:** none (verification only)

- [ ] **Step 1: Run the app in Debug**

Run: `dotnet run --project DiffusionNexus.UI` and open the Generation Gallery tab with at least one enabled folder containing a handful of real images.

- [ ] **Step 2: Build the index**

Click **🏷️ Build Tag Index**. Confirm the busy overlay shows "Indexing images… N/M" counting up, the toolbar pill updates to "X / X indexed" afterward, and (per the download-unification plan, if the WD14 model wasn't already downloaded) the download itself was visible in the Unified Console before indexing started.

- [ ] **Step 3: Confirm tile badges**

Confirm any image the model rated above "general" shows the amber NSFW badge, and hovering a tile reveals up to 3 tag chips above the filename bar.

- [ ] **Step 4: Open Advanced Search and filter**

Click **🔎 Advanced Search**. Confirm the tag cloud is sized/ordered by frequency, clicking a chip highlights it violet and adds it to the active-filter strip above the grid, the gallery grid actually narrows to matching images, and "Clear filters" resets it. Toggle each content-rating radio button and confirm the grid updates accordingly.

- [ ] **Step 5: Confirm filters compose**

With a tag filter active, also type into the existing filename search box and change the date filter — confirm all three filters apply together (AND), not that the tag filter gets silently dropped.

- [ ] **Step 6: Check for regressions**

Confirm favorites, selection, delete, and the existing sort/group options still work unchanged — this task added a filter stage into `ApplySortingAndGroupingAsync`, the most shared piece of code in this file.
