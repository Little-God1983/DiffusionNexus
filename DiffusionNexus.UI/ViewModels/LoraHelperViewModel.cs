using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Search;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Classes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraHelperViewModel : ViewModelBase
{
    // This is the backing list of *all* cards
    private readonly List<LoraCardViewModel> _allCards = new();
    private readonly SearchIndex _searchIndex = new();
    private List<string>? _indexNames;
    private CancellationTokenSource _suggestCts = new();
    private CancellationTokenSource _filterCts = new();
    private List<LoraCardViewModel> _filteredCards = new();
    private bool _isLoadingPage;
    private readonly LoraMetadataDownloadService _metadataDownloader;
    private int _paginationVersion;
    private int _loadedCardCount;
    private const int PageSize = 40;
    private const double ForgePromptStrength = 0.75;
    [ObservableProperty]
    private bool showSuggestions;

    public ObservableCollection<string> SuggestionTokens { get; } = new();

    public IDialogService DialogService { get; set; } = null!;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? searchText;

    [ObservableProperty]
    private FolderItemViewModel? selectedFolder;

    [ObservableProperty]
    private bool showNsfw;

    [ObservableProperty]
    private SortMode sortMode = SortMode.Name;

    [ObservableProperty]
    private bool sortAscending = true;

    public DiffusionModelFilterViewModel DiffusionModelFilter { get; } = new();

    public IRelayCommand ResetFiltersCommand { get; }
    public IAsyncRelayCommand ScanDuplicatesCommand { get; }
    public IAsyncRelayCommand DownloadMissingMetadataCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand SortByNameCommand { get; }
    public IRelayCommand SortByDateCommand { get; }

    // What the View actually binds to
    public ObservableCollection<LoraCardViewModel> Cards { get; } = new();
    public ObservableCollection<FolderItemViewModel> FolderItems { get; } = new();
    public bool HasMoreCards => _loadedCardCount < _filteredCards.Count;
    private readonly ISettingsService _settingsService;
    private Window? _window;
    public LoraHelperViewModel() : this(new SettingsService(), null)
    {
    }

    public LoraHelperViewModel(ISettingsService settingsService, LoraMetadataDownloadService? metadataDownloader = null)
    {
        _settingsService = settingsService;
        _metadataDownloader = metadataDownloader ?? new LoraMetadataDownloadService(new CivitaiApiClient(new HttpClient()));
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        ScanDuplicatesCommand = new AsyncRelayCommand(ScanDuplicatesAsync);
        DownloadMissingMetadataCommand = new AsyncRelayCommand(DownloadMissingMetadataAsync);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        SortByNameCommand = new RelayCommand(() => SortMode = SortMode.Name);
        SortByDateCommand = new RelayCommand(() => SortMode = SortMode.CreationDate);
        DiffusionModelFilter.FiltersChanged += OnDiffusionModelFiltersChanged;
        _ = LoadAsync();
    }
    public void SetWindow(Window window)
    {
        _window = window;
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var settings = await _settingsService.LoadAsync();
            ThumbnailSettings.GenerateVideoThumbnails = settings.GenerateVideoThumbnails;
            ShowNsfw = settings.ShowNsfw;
            var enabledSources = settings.LoraHelperSources
                .Where(source => source.IsEnabled && !string.IsNullOrWhiteSpace(source.FolderPath))
                .Select(source => source.FolderPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (enabledSources.Count == 0)
            {
                return;
            }

            var mergeSources = settings.MergeLoraHelperSources;
            var cardEntries = new List<CardEntry>();
            var discovery = new ModelDiscoveryService();
            var folderNodes = new List<FolderNode>();
            var localProvider = new LocalFileMetadataProvider();

            foreach (var source in enabledSources)
            {
                if (!mergeSources)
                {
                    var node = discovery.BuildFolderTree(source);
                    node.IsExpanded = true;
                    folderNodes.Add(node);
                }

                var reader = new JsonInfoFileReaderService(
                    source,
                    (filePath, progress, cancellationToken) => localProvider.GetModelMetadataAsync(filePath, cancellationToken)
                );
                var sourceModels = await reader.GetModelData(null, CancellationToken.None);

                foreach (var model in sourceModels)
                {
                    var entry = CreateCardEntry(model, source, mergeSources);
                    if (entry != null)
                    {
                        cardEntries.Add(entry);
                    }
                }
            }

            var mergedRoot = mergeSources ? BuildMergedFolderTree(cardEntries) : null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FolderItems.Clear();
                if (mergeSources)
                {
                    if (mergedRoot != null)
                    {
                        FolderItems.Add(ConvertFolder(mergedRoot));
                    }
                }
                else
                {
                    foreach (var node in folderNodes)
                    {
                        FolderItems.Add(ConvertFolder(node));
                    }
                }
            });

            var groupedCards = cardEntries
                .GroupBy(CreateGroupKey)
                .Select(CreateCardFromGroup)
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCards.Clear();
                Cards.Clear();
            });

            foreach (var card in groupedCards)
            {
                card.Parent = this;
                _allCards.Add(card);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DiffusionModelFilter.SetOptions(_allCards.SelectMany(card => card.GetAllDiffusionBaseModels()));
            });

            _filteredCards = _allCards.ToList();
            _loadedCardCount = 0;
            var version = Interlocked.Increment(ref _paginationVersion);
            await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(HasMoreCards)));
            await LoadNextPageAsync(version);

            StartIndexing();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    private static CardEntry? CreateCardEntry(ModelClass model, string sourcePath, bool mergeSources)
    {
        var classification = LoraVariantClassifier.Classify(model);
        var normalizedKey = EnsureNormalizedKey(model, classification.NormalizedKey);
        var variantLabel = string.IsNullOrWhiteSpace(classification.VariantLabel)
            ? LoraVariantClassifier.DefaultVariantLabel
            : classification.VariantLabel;

        var folder = model.AssociatedFilesInfo?.FirstOrDefault()?.DirectoryName;

        if (!mergeSources)
        {
            var entryPath = !string.IsNullOrWhiteSpace(folder) ? folder! : sourcePath;
            return new CardEntry(model, sourcePath, folder, entryPath, null, normalizedKey, variantLabel);
        }

        var segments = LoraHelperTreeBuilder.BuildMergedSegments(sourcePath, folder, model.DiffusionBaseModel);
        if (segments.Count == 0)
        {
            return null;
        }

        var mergedTreePath = string.Join(Path.DirectorySeparatorChar, segments);
        return new CardEntry(model, sourcePath, folder, mergedTreePath, segments, normalizedKey, variantLabel);
    }

    private static string EnsureNormalizedKey(ModelClass model, string normalizedKey)
    {
        if (!string.IsNullOrWhiteSpace(normalizedKey))
        {
            return normalizedKey;
        }

        var fallback = model.SafeTensorFileName;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var alt = LoraVariantClassifier.Classify(fallback).NormalizedKey;
            if (!string.IsNullOrWhiteSpace(alt))
            {
                return alt;
            }
        }

        fallback = model.ModelVersionName;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var alt = LoraVariantClassifier.Classify(fallback).NormalizedKey;
            if (!string.IsNullOrWhiteSpace(alt))
            {
                return alt;
            }
        }

        var baseName = model.SafeTensorFileName ?? model.ModelVersionName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return Guid.NewGuid().ToString("N");
        }

        var cleaned = new string(baseName.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(cleaned)
            ? Guid.NewGuid().ToString("N")
            : cleaned.ToLowerInvariant();
    }

    private static string CreateGroupKey(CardEntry entry)
    {
        var folder = string.IsNullOrWhiteSpace(entry.FolderPath) ? entry.SourcePath : entry.FolderPath;
        var folderKey = folder?.ToLowerInvariant() ?? string.Empty;
        return $"{folderKey}|{entry.NormalizedKey}";
    }

    private LoraCardViewModel CreateCardFromGroup(IGrouping<string, CardEntry> group)
    {
        var primary = group.First();
        var card = new LoraCardViewModel
        {
            FolderPath = primary.FolderPath ?? primary.SourcePath,
            TreePath = primary.TreePath,
        };

        var variants = group.Select(entry => new ModelVariantViewModel(entry.Model, entry.VariantLabel));
        card.InitializeVariants(variants);

        return card;
    }

    private static FolderNode? BuildMergedFolderTree(IEnumerable<CardEntry> entries)
    {
        var segments = entries
            .Where(e => e.TreeSegments != null)
            .Select(e => e.TreeSegments!)
            .ToList();

        return LoraHelperTreeBuilder.BuildMergedFolderTree(segments);
    }

    private sealed record CardEntry(
        ModelClass Model,
        string SourcePath,
        string? FolderPath,
        string TreePath,
        IReadOnlyList<string>? TreeSegments,
        string NormalizedKey,
        string VariantLabel);

    private FolderItemViewModel ConvertFolder(FolderNode node)
    {
        var vm = new FolderItemViewModel
        {
            Name = node.Name,
            ModelCount = node.ModelCount,
            Path = node.FullPath,
            IsExpanded = node.IsExpanded
        };
        foreach (var child in node.Children)
            vm.Children.Add(ConvertFolder(child));
        return vm;
    }

    // 3) This partial method is generated by [ObservableProperty];
    //    it runs whenever SearchText is set.
    partial void OnSearchTextChanged(string? value)
    {
        _ = RefreshCardsAsync();
        DebounceSuggestions();
    }

    partial void OnSelectedFolderChanged(FolderItemViewModel? value)
    {
        if (value != null)
            SearchText = null;
        _ = RefreshCardsAsync();
    }

    partial void OnShowNsfwChanged(bool value)
    {
        _ = RefreshCardsAsync();
    }

    partial void OnSortModeChanged(SortMode value)
    {
        _ = RefreshCardsAsync();
    }

    partial void OnSortAscendingChanged(bool value)
    {
        _ = RefreshCardsAsync();
    }

    private void OnDiffusionModelFiltersChanged(object? sender, EventArgs e)
    {
        _ = RefreshCardsAsync();
    }

    private async Task RefreshCardsAsync()
    {
        _filterCts.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;

        var search = SearchText;
        var folder = SelectedFolder;

        IsLoading = true;
        try
        {
            var list = await Task.Run(() => FilterCards(search, folder), token);
            if (token.IsCancellationRequested)
                return;

            var version = Interlocked.Increment(ref _paginationVersion);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Cards.Clear();
                _filteredCards = list;
                _loadedCardCount = 0;
                OnPropertyChanged(nameof(HasMoreCards));
            });

            await LoadNextPageAsync(version);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
            }
        }
    }

    private List<LoraCardViewModel> FilterCards(string? search, FolderItemViewModel? folder)
    {
        IEnumerable<LoraCardViewModel> query = _allCards;

        if (folder?.Path != null)
            query = query.Where(c =>
                !string.IsNullOrWhiteSpace(c.TreePath) &&
                c.TreePath!.StartsWith(folder.Path!, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            if (_searchIndex.IsReady && _indexNames != null)
            {
                var matches = _searchIndex.SearchPrefix(search!)
                    .Select(i => _allCards[i])
                    .ToHashSet();
                if (matches.Count > 0)
                    query = query.Where(c => matches.Contains(c));
                else
                    query = query.Where(c => MatchesSearch(c, search!));
            }
            else
            {
                query = query.Where(c => MatchesSearch(c, search!));
            }
        }
        Log($"Found: {_allCards.Count(x => x.Variants.Any(v => v.Model.Nsfw == true))} Nsfw Models", LogSeverity.Info);

        if (!ShowNsfw)
            query = query.Where(c => c.HasAnySafeVariant);

        var selectedBaseModels = DiffusionModelFilter.SelectedModels.ToList();
        if (selectedBaseModels.Count > 0)
        {
            var baseModelSet = new HashSet<string>(selectedBaseModels, StringComparer.OrdinalIgnoreCase);
            query = query.Where(card => card.MatchesBaseModel(baseModelSet));
        }

        var sorted = ApplySort(query);
        return sorted.ToList();
    }

    private static bool MatchesSearch(LoraCardViewModel card, string search) =>
        card.MatchesSearch(search);

    public Task LoadNextPageAsync()
    {
        var version = Volatile.Read(ref _paginationVersion);
        return LoadNextPageAsync(version);
    }

    private async Task LoadNextPageAsync(int version)
    {
        if (_isLoadingPage)
        {
            return;
        }

        if (_loadedCardCount >= _filteredCards.Count)
        {
            return;
        }

        _isLoadingPage = true;
        try
        {
            var startIndex = _loadedCardCount;
            var endIndex = Math.Min(startIndex + PageSize, _filteredCards.Count);
            if (endIndex <= startIndex)
            {
                return;
            }

            var batch = _filteredCards
                .Skip(startIndex)
                .Take(endIndex - startIndex)
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _paginationVersion))
                {
                    return;
                }

                foreach (var card in batch)
                {
                    Cards.Add(card);
                }

                _loadedCardCount = endIndex;
                OnPropertyChanged(nameof(HasMoreCards));
            });
        }
        finally
        {
            _isLoadingPage = false;
        }
    }

    private void ResetFilters()
    {
        SelectedFolder = null;
        SearchText = null;
        DiffusionModelFilter.ClearSelection();
        _ = RefreshCardsAsync();
    }

    internal IEnumerable<LoraCardViewModel> ApplySort(IEnumerable<LoraCardViewModel> items)
    {
        IEnumerable<LoraCardViewModel> sorted = SortMode switch
        {
            SortMode.Name => SortAscending
                ? items.OrderBy(c => c.SortKey, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(c => c.SortKey, StringComparer.OrdinalIgnoreCase),
            SortMode.CreationDate => SortAscending
                ? items.OrderBy(c => c.NewestCreationDate)
                : items.OrderByDescending(c => c.NewestCreationDate),
            _ => items
        };

        return sorted;
    }

    /// <summary>
    /// Kick off background construction of the search index. This does not block
    /// the UI and existing filtering logic is used until indexing completes.
    /// </summary>
    private void StartIndexing()
    {
        _indexNames = _allCards
            .Select(c => c.GetSearchIndexText())
            .ToList();
        var namesCopy = _indexNames.ToList();
        Task.Run(() => _searchIndex.Build(namesCopy));
    }

    /// <summary>
    /// Trigger suggestion updates with a 200ms debounce to avoid UI jank while
    /// the user types.
    /// </summary>
    private void DebounceSuggestions()
    {
        _suggestCts.Cancel();
        _suggestCts = new CancellationTokenSource();
        var token = _suggestCts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                await Dispatcher.UIThread.InvokeAsync(UpdateSuggestions);
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    /// <summary>
    /// Refresh the autocomplete suggestion list using the search index.
    /// </summary>
    private void UpdateSuggestions()
    {
        if (!_searchIndex.IsReady || string.IsNullOrWhiteSpace(SearchText))
        {
            SuggestionTokens.Clear();
            ShowSuggestions = false;
            return;
        }

        var list = _searchIndex.Suggest(SearchText!, 10).ToList();
        SuggestionTokens.Clear();
        foreach (var s in list)
            SuggestionTokens.Add(s);
        ShowSuggestions = list.Count > 0;
    }

    /// <summary>
    /// Called from the view when a suggestion is chosen by the user.
    /// </summary>
    public void ApplySuggestion(string suggestion)
    {
        SearchText = suggestion;
        ShowSuggestions = false;
    }

    public async Task DeleteCardAsync(LoraCardViewModel card)
    {
        var variant = card.SelectedVariant;
        if (DialogService == null || variant?.Model == null)
            return;

        var confirm = await DialogService.ShowConfirmationAsync($"Delete '{variant.Model.SafeTensorFileName}'?");
        if (confirm != true) return;

        foreach (var file in variant.Model.AssociatedFilesInfo)
        {
            try { File.Delete(file.FullName); } catch { }
        }

        var removed = card.RemoveVariant(variant);
        if (!removed)
        {
            return;
        }

        if (card.Variants.Count == 0)
        {
            _allCards.Remove(card);
            _filteredCards.Remove(card);
            Cards.Remove(card);
            _loadedCardCount = Cards.Count;
            OnPropertyChanged(nameof(HasMoreCards));
        }

        StartIndexing();
        DiffusionModelFilter.SetOptions(_allCards.SelectMany(c => c.GetAllDiffusionBaseModels()));
    }

    public async Task OpenWebForCardAsync(LoraCardViewModel card)
    {
        var model = card.SelectedVariant?.Model;
        if (model == null)
            return;

        var settings = await _settingsService.LoadAsync();
        var apiKey = settings.CivitaiApiKey ?? string.Empty;
        string? id;
        if (string.IsNullOrWhiteSpace(model.ModelId))
        {
            var result = await _metadataDownloader.EnsureMetadataAsync(model, apiKey);
            id = result.ModelId;
            if (string.IsNullOrWhiteSpace(id))
            {
                Log($"Can't open Link. No Id found for {model.ModelVersionName}", LogSeverity.Error);
                return;

            }
        }
        else
        {
            // If we already have the ID, just use it
            id = model.ModelId;
        }

        var url = $"https://civitai.com/models/{id}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public async Task CopyTrainedWordsAsync(LoraCardViewModel card)
    {
        var model = card.SelectedVariant?.Model;
        if (model == null)
            return;

        var settings = await _settingsService.LoadAsync();
        var apiKey = settings.CivitaiApiKey ?? string.Empty;
        await _metadataDownloader.EnsureMetadataAsync(model, apiKey);

        if (model.TrainedWords.Count == 0 && (!settings.UseForgeStylePrompts))
        {
            Log($"No trained words for {model.ModelVersionName}", LogSeverity.Warning);
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { Clipboard: { } clipboard })
        {
            try
            {
                var text = string.Join(", ", model.TrainedWords).TrimEnd();
                if (settings.UseForgeStylePrompts)
                {
                    var name = model.SafeTensorFileName;
                    text = $"<lora:{name}:{ForgePromptStrength.ToString(System.Globalization.CultureInfo.InvariantCulture)}> " + text;
                }
                await clipboard.SetTextAsync(text);
                Log($"Trigger words for: {GetLoraNameShort(card)}  copied to clipboard", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                Log($"failed to copy: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    private static IEnumerable<char> GetLoraNameShort(LoraCardViewModel card)
    {
        string input = card.SelectedVariant?.Model.ModelVersionName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return input;

        const int hardLimit = 30;
        const int softLimit = 20;

        if (input.Length <= softLimit)
            return input;

        string trimmed = input.Substring(0, Math.Min(hardLimit, input.Length));

        int lastSpaceBeforeHardLimit = trimmed.LastIndexOf(' ');
        if (softLimit < trimmed.Length && char.IsLetterOrDigit(trimmed[softLimit]) && lastSpaceBeforeHardLimit > softLimit)
        {
            // Cut at last space before exceeding softLimit and still inside the hardLimit
            trimmed = trimmed.Substring(0, lastSpaceBeforeHardLimit);
        }
        else if (trimmed.Length > softLimit && char.IsLetterOrDigit(trimmed[softLimit]))
        {
            // Extend to next space (to complete the word), within the 30 char limit
            int nextSpace = input.IndexOf(' ', softLimit);
            if (nextSpace != -1 && nextSpace <= hardLimit)
                trimmed = input.Substring(0, nextSpace);
            else
                trimmed = input.Substring(0, Math.Min(hardLimit, input.Length));
        }

        if (input.Length > trimmed.Length)
            return trimmed + "...";

        return trimmed;
    }

    public async Task CopyModelNameAsync(LoraCardViewModel card)
    {
        var model = card.SelectedVariant?.Model;
        if (model == null)
            return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { Clipboard: { } clipboard })
        {
            try
            {
                var text = model.SafeTensorFileName;
                await clipboard.SetTextAsync(model.SafeTensorFileName);
                Log($"Filename: {text} copied to clipboard", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                Log($"failed to copy filename: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    private async Task ScanDuplicatesAsync()
    {
        if (_window is null) return;
        var settings = await _settingsService.LoadAsync();
        var options = new FolderPickerOpenOptions();
        var firstPath = settings.LoraHelperSources
            .FirstOrDefault(source => source.IsEnabled && !string.IsNullOrWhiteSpace(source.FolderPath))?
            .FolderPath;
        if (!string.IsNullOrWhiteSpace(firstPath))
        {
            var start = await _window.StorageProvider.TryGetFolderFromPathAsync(firstPath);
            if (start != null)
                options.SuggestedStartLocation = start;
        }
        var pick = await _window.StorageProvider.OpenFolderPickerAsync(options);
        var path = pick.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsLoading = true;
        try
        {
            var scanner = new DuplicateScanner();
            var progress = new Progress<ScanProgress>(_ => { });
            await Task.Run(() => scanner.ScanAsync(path, progress, CancellationToken.None));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string ComputeSHA256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }

    private async Task DownloadMissingMetadataAsync()
    {
        IsLoading = true;
        try
        {
            var settings = await _settingsService.LoadAsync();
            var apiKey = settings.CivitaiApiKey ?? string.Empty;

            var missing = _allCards
                .SelectMany(card => card.Variants)
                .Where(variant => variant.Model != null && !variant.Model.HasFullMetadata)
                .ToList();

            Log($"{missing.Count} models missing metadata", LogSeverity.Info);

            foreach (var variant in missing)
            {
                var model = variant.Model;
                if (model == null) continue;

                Log($"Requesting metadata for {model.ModelVersionName}", LogSeverity.Info);
                var result = await _metadataDownloader.EnsureMetadataAsync(model, apiKey);
                switch (result.ResultType)
                {
                    case MetadataDownloadResultType.AlreadyExists:
                        Log($"{model.ModelVersionName}: already has metadata", LogSeverity.Info);
                        break;
                    case MetadataDownloadResultType.Downloaded:
                        Log($"{model.ModelVersionName}: metadata downloaded", LogSeverity.Success);
                        break;
                    case MetadataDownloadResultType.NotFound:
                        Log($"{model.ModelVersionName}: not found on Civitai", LogSeverity.Error);
                        break;
                    case MetadataDownloadResultType.Error:
                        Log($"{model.ModelVersionName}: failed to download metadata - {result.ErrorMessage}", LogSeverity.Error);
                        break;
                }
            }

            await LoadAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }
}


