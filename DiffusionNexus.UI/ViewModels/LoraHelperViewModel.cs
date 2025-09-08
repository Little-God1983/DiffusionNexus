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

    [ObservableProperty]
    private WanVideoGroupingMode wanVideoGrouping = WanVideoGroupingMode.No;

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
    private SettingsModel? _settings;
    private string? _rootFolder;
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
            _settings = await _settingsService.LoadAsync();
            ThumbnailSettings.GenerateVideoThumbnails = _settings.GenerateVideoThumbnails;
            ShowNsfw = _settings.ShowNsfw;
            _rootFolder = _settings.LoraHelperFolderPath;
            if (string.IsNullOrWhiteSpace(_rootFolder))
            {
                return;
            }

            WanVideoGrouping = _settings.WanVideoGrouping;

            var localProvider = new LocalFileMetadataProvider();
            // Fix for CS1503: Argument 2: cannot convert from 'method group' to 'System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<DiffusionNexus.Service.Classes.ModelClass>>'

            // The issue is that the method group `localProvider.GetModelMetadataAsync` does not match the expected delegate signature.
            // To fix this, explicitly create a lambda expression that matches the expected signature.
            var reader = new JsonInfoFileReaderService(
                _rootFolder!,
                (filePath, progress, cancellationToken) => localProvider.GetModelMetadataAsync(filePath, cancellationToken)
            );
            var models = await reader.GetModelData(null, CancellationToken.None);

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
            ApplyWanVideoGrouping();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
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

    partial void OnWanVideoGroupingChanged(WanVideoGroupingMode value)
    {
        ApplyWanVideoGrouping();
        if (_settings != null)
        {
            _settings.WanVideoGrouping = value;
            _ = _settingsService.SaveAsync(_settings);
        }
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
            query = query.Where(c =>
                (folder.Path == null || (c.FolderPath != null && c.FolderPath.StartsWith(folder.Path!, StringComparison.OrdinalIgnoreCase))) &&
                (folder.Filter == null || folder.Filter(c)));

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

    private void ApplyWanVideoGrouping()
    {
        if (_rootFolder == null)
            return;

        var expandStates = new Dictionary<string, bool>();
        var selectedPath = SelectedFolder?.Path;
        if (FolderItems.FirstOrDefault() is { } currentRoot)
            CaptureExpandStates(currentRoot, expandStates);

        FolderItemViewModel root;
        var wanAll = _allCards.Where(c => c.Model != null && IsWanVideo(c.Model!)).ToList();
        var others = _allCards.Where(c => c.Model == null || !IsWanVideo(c.Model!)).ToList();

        switch (WanVideoGrouping)
        {
            case WanVideoGroupingMode.ByType:
                var i2vCards = wanAll.Where(c => c.Model != null && IsI2V(c.Model!)).ToList();
                var t2vCards = wanAll.Where(c => c.Model != null && IsT2V(c.Model!)).ToList();
                root = BuildTreeFromCards(others, _rootFolder, null);
                if (i2vCards.Count > 0)
                {
                    var groupRoot = BuildTreeFromCards(i2vCards, _rootFolder, c => c.Model != null && IsI2V(c.Model!));
                    var inner = groupRoot.Children.FirstOrDefault(c => c.Name.StartsWith("Wan", StringComparison.OrdinalIgnoreCase));
                    var node = new FolderItemViewModel
                    {
                        Name = "Wan Video • I2V",
                        Path = Path.Combine(_rootFolder, "Wan Video • I2V"),
                        ModelCount = groupRoot.ModelCount,
                        Filter = c => c.Model != null && IsI2V(c.Model!)
                    };
                    foreach (var child in (inner?.Children ?? groupRoot.Children))
                        node.Children.Add(child);
                    root.Children.Add(node);
                }
                if (t2vCards.Count > 0)
                {
                    var groupRoot = BuildTreeFromCards(t2vCards, _rootFolder, c => c.Model != null && IsT2V(c.Model!));
                    var inner = groupRoot.Children.FirstOrDefault(c => c.Name.StartsWith("Wan", StringComparison.OrdinalIgnoreCase));
                    var node = new FolderItemViewModel
                    {
                        Name = "Wan Video • T2V",
                        Path = Path.Combine(_rootFolder, "Wan Video • T2V"),
                        ModelCount = groupRoot.ModelCount,
                        Filter = c => c.Model != null && IsT2V(c.Model!)
                    };
                    foreach (var child in (inner?.Children ?? groupRoot.Children))
                        node.Children.Add(child);
                    root.Children.Add(node);
                }
                root.ModelCount = root.Children.Sum(c => c.ModelCount);
                break;
            case WanVideoGroupingMode.All:
                var allRoot = BuildTreeFromCards(others, _rootFolder, null);
                if (wanAll.Count > 0)
                {
                    var groupRoot = BuildTreeFromCards(wanAll, _rootFolder, c => c.Model != null && IsWanVideo(c.Model!));
                    var inner = groupRoot.Children.FirstOrDefault(c => c.Name.StartsWith("Wan", StringComparison.OrdinalIgnoreCase));
                    var node = new FolderItemViewModel
                    {
                        Name = "Wan Video",
                        Path = Path.Combine(_rootFolder, "Wan Video"),
                        ModelCount = groupRoot.ModelCount,
                        Filter = c => c.Model != null && IsWanVideo(c.Model!)
                    };
                    foreach (var child in (inner?.Children ?? groupRoot.Children))
                        node.Children.Add(child);
                    allRoot.Children.Add(node);
                    allRoot.ModelCount = allRoot.Children.Sum(c => c.ModelCount);
                }
                root = allRoot;
                break;
            default:
                root = BuildTreeFromCards(_allCards, _rootFolder, null);
                break;
        }

        RestoreExpandStates(root, expandStates);
        var newSelected = FindByPath(root, selectedPath);

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            FolderItems.Clear();
            FolderItems.Add(root);
            SelectedFolder = newSelected;
        });
    }

    private FolderItemViewModel BuildTreeFromCards(IEnumerable<LoraCardViewModel> cards, string rootPath, Func<LoraCardViewModel, bool>? predicate)
    {
        var root = new FolderItemViewModel { Name = Path.GetFileName(rootPath), Path = rootPath, Filter = predicate };
        foreach (var card in cards)
        {
            if (card.FolderPath == null) continue;
            var relative = Path.GetRelativePath(rootPath, card.FolderPath);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(p => !string.IsNullOrWhiteSpace(p));
            var node = root;
            node.ModelCount++;
            var currentPath = rootPath;
            foreach (var part in parts)
            {
                currentPath = Path.Combine(currentPath, part);
                var child = node.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
                if (child == null)
                {
                    child = new FolderItemViewModel { Name = part, Path = currentPath, Filter = predicate };
                    node.Children.Add(child);
                }
                child.ModelCount++;
                node = child;
            }
        }
        return root;
    }

    private static void CaptureExpandStates(FolderItemViewModel node, Dictionary<string, bool> dict)
    {
        if (node.Path != null)
            dict[node.Path] = node.IsExpanded;
        foreach (var child in node.Children)
            CaptureExpandStates(child, dict);
    }

    private static void RestoreExpandStates(FolderItemViewModel node, Dictionary<string, bool> dict)
    {
        if (node.Path != null && dict.TryGetValue(node.Path, out var expanded))
            node.IsExpanded = expanded;
        foreach (var child in node.Children)
            RestoreExpandStates(child, dict);
    }

    private static FolderItemViewModel? FindByPath(FolderItemViewModel node, string? path)
    {
        if (path == null)
            return null;
        if (node.Path == path)
            return node;
        foreach (var child in node.Children)
        {
            var found = FindByPath(child, path);
            if (found != null)
                return found;
        }
        return null;
    }

    private static bool IsWanVideo(ModelClass model) =>
        model.DiffusionBaseModel?.StartsWith("Wan Video", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsI2V(ModelClass model) =>
        BaseModelHasToken(model.DiffusionBaseModel, "I2V");

    private static bool IsT2V(ModelClass model) =>
        BaseModelHasToken(model.DiffusionBaseModel, "T2V");

    private static bool BaseModelHasToken(string? baseModel, string token)
    {
        if (string.IsNullOrWhiteSpace(baseModel))
            return false;
        var norm = baseModel.Replace('-', ' ');
        var parts = norm.Split(new[] { ' ', '.', ',', ';', ':', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => string.Equals(p, token, StringComparison.OrdinalIgnoreCase));
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
        if (!string.IsNullOrWhiteSpace(settings.LoraHelperFolderPath))
        {
            var start = await _window.StorageProvider.TryGetFolderFromPathAsync(settings.LoraHelperFolderPath);
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


