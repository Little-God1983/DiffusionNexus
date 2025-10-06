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
    private int _nextIndex;
    private bool _isLoadingPage;
    private const int PageSize = 50;
    private readonly LoraMetadataDownloadService _metadataDownloader;
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

    public IRelayCommand ResetFiltersCommand { get; }
    public IAsyncRelayCommand ScanDuplicatesCommand { get; }
    public IAsyncRelayCommand DownloadMissingMetadataCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand SortByNameCommand { get; }
    public IRelayCommand SortByDateCommand { get; }

    // What the View actually binds to
    public ObservableCollection<LoraCardViewModel> Cards { get; } = new();
    public ObservableCollection<FolderItemViewModel> FolderItems { get; } = new();
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
            var mergeSources = settings.MergeLoraHelperSources;
            var enabledSources = settings.LoraHelperSources
                .Where(source => source.IsEnabled && !string.IsNullOrWhiteSpace(source.FolderPath))
                .Select(source => source.FolderPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (enabledSources.Count == 0)
            {
                return;
            }

            List<FolderNode>? rootNodes = null;
            var discovery = new ModelDiscoveryService();
            if (!mergeSources)
            {
                rootNodes = await Task.Run(() =>
                    enabledSources.Select(path =>
                    {
                        var node = discovery.BuildFolderTree(path);
                        node.IsExpanded = true;
                        return node;
                    }).ToList());
            }

            var localProvider = new LocalFileMetadataProvider();
            var models = new List<ModelClass>();
            foreach (var source in enabledSources)
            {
                var reader = new JsonInfoFileReaderService(
                    source,
                    (filePath, progress, cancellationToken) => localProvider.GetModelMetadataAsync(filePath, cancellationToken)
                );
                var sourceModels = await reader.GetModelData(null, CancellationToken.None);
                models.AddRange(sourceModels);
            }

            var folderViewModels = mergeSources
                ? BuildMergedFolderItems(models, enabledSources)
                : rootNodes?.Select(ConvertFolder).ToList() ?? new List<FolderItemViewModel>();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FolderItems.Clear();
                foreach (var node in folderViewModels)
                {
                    FolderItems.Add(node);
                }
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCards.Clear();
                Cards.Clear();
            });

            foreach (var model in models)
            {
                var folder = model.AssociatedFilesInfo.FirstOrDefault()?.DirectoryName;
                var card = new LoraCardViewModel { Model = model, FolderPath = folder, Parent = this };
                _allCards.Add(card);
            }

            _filteredCards = _allCards.ToList();
            _nextIndex = 0;
            await LoadNextPageAsync();

            StartIndexing();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    private FolderItemViewModel ConvertFolder(FolderNode node)
    {
        var vm = new FolderItemViewModel
        {
            Name = node.Name,
            ModelCount = node.ModelCount,
            Path = node.FullPath,
            IsExpanded = node.IsExpanded
        };
        if (!string.IsNullOrWhiteSpace(node.FullPath))
            vm.Paths.Add(node.FullPath);
        foreach (var child in node.Children)
        {
            var childVm = ConvertFolder(child);
            vm.Children.Add(childVm);
            foreach (var path in childVm.Paths)
                vm.Paths.Add(path);
        }
        return vm;
    }

    private List<FolderItemViewModel> BuildMergedFolderItems(List<ModelClass> models, IReadOnlyList<string> enabledSources)
    {
        var root = new MergedTreeNode("Loras");
        var orderedSources = enabledSources
            .OrderByDescending(s => s.Length)
            .ToList();
        foreach (var model in models)
        {
            var folderPath = model.AssociatedFilesInfo.FirstOrDefault()?.DirectoryName;
            if (string.IsNullOrWhiteSpace(folderPath))
                continue;

            var sourceRoot = orderedSources.FirstOrDefault(src =>
                folderPath.StartsWith(src, StringComparison.OrdinalIgnoreCase));
            if (sourceRoot is null)
                continue;

            var baseModel = NormalizeBaseModel(model.DiffusionBaseModel);
            var baseNodeName = baseModel ?? $"{GetSourceDisplayName(sourceRoot)} (Unmerged)";

            var relative = Path.GetRelativePath(sourceRoot, folderPath);
            if (relative == ".")
                relative = string.Empty;
            var segments = SplitPath(relative);
            var startIndex = 0;
            if (baseModel != null && segments.Length > 0 &&
                string.Equals(segments[0], baseModel, StringComparison.OrdinalIgnoreCase))
            {
                startIndex = 1;
            }

            AddModelToMergedTree(root, baseNodeName, sourceRoot, segments, startIndex, folderPath);
        }

        if (root.Children.Count == 0)
            return new List<FolderItemViewModel>();

        return new List<FolderItemViewModel> { ConvertMergedNode(root, true) };
    }

    private static string GetSourceDisplayName(string sourceRoot)
    {
        var trimmed = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private static string? NormalizeBaseModel(string? baseModel)
    {
        if (string.IsNullOrWhiteSpace(baseModel))
            return null;

        return string.Equals(baseModel, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? null
            : baseModel.Trim();
    }

    private static string[] SplitPath(string relative)
    {
        return string.IsNullOrWhiteSpace(relative)
            ? Array.Empty<string>()
            : relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void AddModelToMergedTree(
        MergedTreeNode root,
        string baseNodeName,
        string sourceRoot,
        IReadOnlyList<string> segments,
        int startIndex,
        string folderPath)
    {
        root.ModelCount++;
        root.Paths.Add(folderPath);

        var baseNode = root.GetOrAddChild(baseNodeName);
        baseNode.ModelCount++;
        baseNode.Paths.Add(folderPath);

        if (segments.Count > 0)
        {
            var basePathSegments = new string[Math.Min(1, segments.Count) + 1];
            basePathSegments[0] = sourceRoot;
            if (segments.Count > 0)
                basePathSegments[1] = segments[0];
            var basePath = Path.Combine(basePathSegments.Where(s => !string.IsNullOrEmpty(s)).ToArray());
            if (!string.IsNullOrWhiteSpace(basePath))
                baseNode.Paths.Add(basePath);
        }

        var current = baseNode;
        for (var i = startIndex; i < segments.Count; i++)
        {
            var segment = segments[i];
            var child = current.GetOrAddChild(segment);
            child.ModelCount++;
            var combine = new string[i + 2];
            combine[0] = sourceRoot;
            for (var j = 0; j <= i; j++)
                combine[j + 1] = segments[j];
            var actualPath = Path.Combine(combine);
            if (!string.IsNullOrWhiteSpace(actualPath))
                child.Paths.Add(actualPath);
            current = child;
        }
    }

    private FolderItemViewModel ConvertMergedNode(MergedTreeNode node, bool isRoot = false)
    {
        var vm = new FolderItemViewModel
        {
            Name = node.Name,
            ModelCount = node.ModelCount,
            IsExpanded = isRoot || node.ModelCount > 0
        };

        if (!isRoot && node.Paths.Count == 1)
            vm.Path = node.Paths.First();

        foreach (var path in node.Paths)
            vm.Paths.Add(path);

        var orderedChildren = node.Children.Values
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var child in orderedChildren)
        {
            var childVm = ConvertMergedNode(child);
            vm.Children.Add(childVm);
        }

        return vm;
    }

    private sealed class MergedTreeNode
    {
        public MergedTreeNode(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public Dictionary<string, MergedTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ModelCount { get; set; }

        public MergedTreeNode GetOrAddChild(string name)
        {
            if (!Children.TryGetValue(name, out var child))
            {
                child = new MergedTreeNode(name);
                Children[name] = child;
            }

            return child;
        }
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

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Cards.Clear();
                _filteredCards = list;
                _nextIndex = 0;
            });

            await LoadNextPageAsync();
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

        if (folder != null)
        {
            var candidatePaths = folder.Paths.Count > 0
                ? folder.Paths.ToArray()
                : folder.Path != null
                    ? new[] { folder.Path }
                    : Array.Empty<string>();

            if (candidatePaths.Length > 0)
            {
                query = query.Where(c =>
                    c.FolderPath != null && candidatePaths.Any(p =>
                        c.FolderPath!.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            }
        }

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
        Log($"Found: {_allCards.Where(x => x.Model.Nsfw == true).Count()} Nsfw Models", LogSeverity.Info);

        if (!ShowNsfw)
            query = query.Where(c => c.Model?.Nsfw != true);

        var sorted = ApplySort(query);
        return sorted.ToList();
    }

    private static bool MatchesSearch(LoraCardViewModel card, string search) =>
        card.Model.SafeTensorFileName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
        card.Model.ModelVersionName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    public async Task LoadNextPageAsync()
    {
        if (_isLoadingPage)
            return;

        if (_nextIndex >= _filteredCards.Count)
            return;

        _isLoadingPage = true;
        var slice = _filteredCards.Skip(_nextIndex).Take(PageSize).ToList();
        _nextIndex += slice.Count;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var card in slice)
                Cards.Add(card);
        });

        _isLoadingPage = false;
    }

    private void ResetFilters()
    {
        SelectedFolder = null;
        SearchText = null;
        _ = RefreshCardsAsync();
    }

    internal IEnumerable<LoraCardViewModel> ApplySort(IEnumerable<LoraCardViewModel> items)
    {
        IEnumerable<LoraCardViewModel> sorted = SortMode switch
        {
            SortMode.Name => SortAscending
                ? items.OrderBy(c => c.Model?.SafeTensorFileName, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(c => c.Model?.SafeTensorFileName, StringComparer.OrdinalIgnoreCase),
            SortMode.CreationDate => SortAscending
                ? items.OrderBy(GetCreationDate)
                : items.OrderByDescending(GetCreationDate),
            _ => items
        };

        return sorted;
    }

    internal static DateTime GetCreationDate(LoraCardViewModel card)
    {
        var file = card.Model?.AssociatedFilesInfo.FirstOrDefault(f =>
            f.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            f.Extension.Equals(".pt", StringComparison.OrdinalIgnoreCase));
        return file?.CreationTime ?? DateTime.MinValue;
    }

    /// <summary>
    /// Kick off background construction of the search index. This does not block
    /// the UI and existing filtering logic is used until indexing completes.
    /// </summary>
    private void StartIndexing()
    {
        _indexNames = _allCards
            .Select(c => $"{c.Model.SafeTensorFileName ?? string.Empty} {c.Model.ModelVersionName ?? string.Empty}")
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
        if (DialogService == null || card.Model == null)
            return;

        var confirm = await DialogService.ShowConfirmationAsync($"Delete '{card.Model.SafeTensorFileName}'?");
        if (confirm != true) return;

        foreach (var file in card.Model.AssociatedFilesInfo)
        {
            try { File.Delete(file.FullName); } catch { }
        }

        _allCards.Remove(card);
        Cards.Remove(card);
        StartIndexing();
    }

    public async Task OpenWebForCardAsync(LoraCardViewModel card)
    {
        if (card.Model == null)
            return;

        var settings = await _settingsService.LoadAsync();
        var apiKey = settings.CivitaiApiKey ?? string.Empty;
        string? id;
        if (string.IsNullOrWhiteSpace(card.Model.ModelId))
        {
            var result = await _metadataDownloader.EnsureMetadataAsync(card.Model, apiKey);
            id = result.ModelId;
            if (string.IsNullOrWhiteSpace(id))
            {
                Log($"Can't open Link. No Id found for {card.Model.ModelVersionName}", LogSeverity.Error);
                return;

            }
        }
        else
        {
            // If we already have the ID, just use it
            id = card.Model.ModelId;
        }

        var url = $"https://civitai.com/models/{id}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public async Task CopyTrainedWordsAsync(LoraCardViewModel card)
    {
        if (card.Model == null)
            return;

        var settings = await _settingsService.LoadAsync();
        var apiKey = settings.CivitaiApiKey ?? string.Empty;
        await _metadataDownloader.EnsureMetadataAsync(card.Model, apiKey);

        if (card.Model.TrainedWords.Count == 0 && (!settings.UseForgeStylePrompts))
        {
            Log($"No trained words for {card.Model.ModelVersionName}", LogSeverity.Warning);
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { Clipboard: { } clipboard })
        {
            try
            {
                var text = string.Join(", ", card.Model.TrainedWords).TrimEnd();
                if (settings.UseForgeStylePrompts)
                {
                    var name = card.Model.SafeTensorFileName;
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
        string input = card.Model?.ModelVersionName ?? string.Empty;
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
        if (card.Model == null)
            return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { Clipboard: { } clipboard })
        {
            try
            {
                var text = card.Model.SafeTensorFileName;
                await clipboard.SetTextAsync(card.Model.SafeTensorFileName);
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

            var missing = _allCards.Where(c => c.Model != null && !c.Model.HasFullMetadata).ToList();
            Log($"{missing.Count} models missing metadata", LogSeverity.Info);

            foreach (var card in missing)
            {
                if (card.Model == null) continue;
                Log($"Requesting metadata for {card.Model.ModelVersionName}", LogSeverity.Info);
                var result = await _metadataDownloader.EnsureMetadataAsync(card.Model, apiKey);
                switch (result.ResultType)
                {
                    case MetadataDownloadResultType.AlreadyExists:
                        Log($"{card.Model.ModelVersionName}: already has metadata", LogSeverity.Info);
                        break;
                    case MetadataDownloadResultType.Downloaded:
                        Log($"{card.Model.ModelVersionName}: metadata downloaded", LogSeverity.Success);
                        break;
                    case MetadataDownloadResultType.NotFound:
                        Log($"{card.Model.ModelVersionName}: not found on Civitai", LogSeverity.Error);
                        break;
                    case MetadataDownloadResultType.Error:
                        Log($"{card.Model.ModelVersionName}: failed to download metadata - {result.ErrorMessage}", LogSeverity.Error);
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


