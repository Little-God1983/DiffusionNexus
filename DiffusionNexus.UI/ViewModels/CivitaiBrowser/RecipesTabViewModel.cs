using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiRecipe;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>Which cards the status line lets through.</summary>
public enum RecipeStatusFilter { All, Ready, Missing }

/// <summary>
/// The Recipes tab's brain (#667): the recipes on disk as History and Kept, the counter, the filters,
/// the date groups and the actions on the selection. The app never deletes a recipe by itself; only
/// Delete and Clear history do, after the user said yes.
/// </summary>
public sealed partial class RecipesTabViewModel : ObservableObject, IDisposable
{
    private const string LogSource = "CivitaiRecipe";
    private const int OpenWithoutAsking = 5;

    private readonly RecipeStore _store;
    private readonly IRecipeHost _host;
    private readonly IDialogService? _dialogs;
    private readonly IUnifiedLogger? _logger;
    private readonly Func<DateTime> _nowLocal;
    private readonly IUiScheduler _ui;

    private List<RecipeCardViewModel> _cards = [];
    private List<RecipeCardViewModel> _visible = [];
    private RecipeCardViewModel? _anchor;
    private bool _quiet;

    [ObservableProperty] private bool _isKeptView;
    [ObservableProperty] private int _historyCount;
    [ObservableProperty] private int _keptCount;
    [ObservableProperty] private RecipeStatusFilter _statusFilter;
    [ObservableProperty] private bool _showNsfw = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private string? _hiddenText;
    [ObservableProperty] private string? _emptyText;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _queueButtonText = "Queue missing LoRAs (0)";
    [ObservableProperty] private string _queueTooltip = QueueTooltipDefault;

    /// <summary>What the last action could not do; shown beside the counter.</summary>
    [ObservableProperty] private string? _statusMessage;

    private const string QueueTooltipDefault = "Queues the LoRAs these recipes are missing, each once.";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRecipeOpen))] private CivitaiRecipeViewModel? _openRecipe;

    private readonly IClipboardService? _clipboard;
    private readonly Func<CivitaiImageRecipeService>? _retryService;
    private readonly HashSet<long> _retried = [];
    private CancellationTokenSource? _openCts;
    private bool _disposed;

    public RecipesTabViewModel(RecipeStore store, IRecipeHost host, IDialogService? dialogs, IUnifiedLogger? logger,
        Func<DateTime>? nowLocal = null, IClipboardService? clipboard = null, Func<CivitaiImageRecipeService>? retryService = null,
        IUiScheduler? uiScheduler = null)
    {
        _store = store;
        _host = host;
        _dialogs = dialogs;
        _logger = logger;
        _nowLocal = nowLocal ?? (() => DateTime.Now);
        _clipboard = clipboard;
        _retryService = retryService;
        _ui = uiScheduler ?? AvaloniaUiScheduler.Instance;

        // Built with the LoRA Viewer on the UI thread: the first read of the disk must not hold it up.
        RequestReload();
        _store.Changed += OnStoreChanged;
        _host.InstalledIndexChanged += OnInstalledChanged;
    }

    public ObservableCollection<RecipeGroupViewModel> Groups { get; } = [];

    /// <summary>
    /// Called when the tab is shown: it can be opened before Browse Civitai ever was, and until the host has
    /// read the library every card says "Checking library…" and nothing can be queued.
    /// </summary>
    public Task ActivateAsync() => _host.EnsureInstalledLoadedAsync();

    /// <summary>Whether the cards are judged against the real library yet.</summary>
    public bool IsLibraryRead => _host.IsInstalledLoaded;

    /// <summary>"1 recipe" / "3 recipes", the one wording every text of the tab uses.</summary>
    public static string Recipes(int count) => count == 1 ? "1 recipe" : $"{count} recipes";

    public void Dispose()
    {
        _disposed = true;
        _openCts?.Cancel();
        _searchCts?.Cancel();
        ReleaseCards(OpenRecipe);
        _store.Changed -= OnStoreChanged;
        _host.InstalledIndexChanged -= OnInstalledChanged;
    }

    // Any thread: the browser saves inside Task.Run, the tab's own commands on the UI thread.
    private void OnStoreChanged(object? sender, EventArgs e) => RequestReload();

    private void OnInstalledChanged(object? sender, EventArgs e)
    {
        var installed = _host.Installed;
        foreach (var card in _cards) card.ApplyStatus(installed, IsLibraryRead);
        if (OpenRecipe is { } open)
        {
            open.ApplyInstalledIndex(installed);
            open.IsLibraryRead = IsLibraryRead;
        }
        OnPropertyChanged(nameof(IsLibraryRead));
        QueueMissingCommand.NotifyCanExecuteChanged();
        Rebuild();
    }

    partial void OnStatusFilterChanged(RecipeStatusFilter value) => Rebuild();

    /// <summary>How long typing must pause before the list is rebuilt; filter toggles rebuild at once.</summary>
    internal static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _searchCts;

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        SearchTask = RebuildAfterTypingAsync(_searchCts.Token);
    }

    private async Task RebuildAfterTypingAsync(CancellationToken ct)
    {
        try { await Task.Delay(SearchDebounce, ct); }
        catch (OperationCanceledException) { return; }
        await _ui.InvokeAsync(() =>
        {
            if (!ct.IsCancellationRequested && !_disposed) Rebuild();
        });
    }

    partial void OnShowNsfwChanged(bool value)
    {
        _logger?.Info(LogCategory.General, LogSource, $"Recipes tab: Show NSFW turned {(value ? "on" : "off")}.");
        Rebuild();
    }

    #region Loading and filtering

    private readonly object _reloadGate = new();
    private bool _reloading;
    private bool _reloadAgain;

    /// <summary>The reload in flight, or the last one; tests wait on it.</summary>
    internal Task ReloadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The rebuild waiting for typing to pause, or the last one; tests wait on it.</summary>
    internal Task SearchTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads the disk off the UI thread and applies the result on it. A request while a reload runs is
    /// folded into exactly one more pass after it, however many arrive. Any thread.
    /// </summary>
    private void RequestReload()
    {
        lock (_reloadGate)
        {
            if (_reloading)
            {
                _reloadAgain = true;
                return;
            }
            _reloading = true;
            ReloadTask = Task.Run(ReloadPassesAsync);
        }
    }

    private async Task ReloadPassesAsync()
    {
        try
        {
            while (true)
            {
                var loaded = _store.LoadAll();
                if (!_disposed) await _ui.InvokeAsync(() => { if (!_disposed) Apply(loaded); });
                lock (_reloadGate)
                {
                    if (!_reloadAgain || _disposed)
                    {
                        _reloading = false;
                        return;
                    }
                    _reloadAgain = false;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_reloadGate)
            {
                _reloading = false;
                _reloadAgain = false;
            }
            _logger?.Warn(LogCategory.FileSystem, LogSource, $"The recipes could not be read: {ex.Message}", ex.ToString());
        }
    }

    private void Apply(IReadOnlyList<StoredRecipe> loaded)
    {
        var now = _nowLocal();
        var installed = _host.Installed;
        var previous = _cards.ToDictionary(c => c.ImageId);
        var cards = new List<RecipeCardViewModel>();
        _quiet = true;
        foreach (var stored in loaded)
        {
            var card = BuildCard(stored, now);
            card.ApplyStatus(installed, IsLibraryRead);
            if (previous.TryGetValue(card.ImageId, out var old)) card.CopyStateFrom(old);
            cards.Add(card);
        }
        _quiet = false;
        if (_anchor is not null) _anchor = cards.FirstOrDefault(c => c.ImageId == _anchor.ImageId);
        _cards = cards;

        KeptCount = cards.Count(c => c.IsKept);
        HistoryCount = cards.Count - KeptCount;
        CounterText = $"{Recipes(cards.Count)} · {FileSizeFormatter.Format(cards.Sum(c => c.Stored.SizeBytes))} on disk " +
                      $"(History {HistoryCount}, Kept {KeptCount})";
        Rebuild();
    }

    /// <summary>A recipe the card cannot show (a member deeper than the store checks is null) is a broken card, not a tab that fails to build.</summary>
    private RecipeCardViewModel BuildCard(StoredRecipe stored, DateTime now)
    {
        try
        {
            return new RecipeCardViewModel(stored, now, OnSelectionChanged);
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentException or InvalidOperationException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {stored.ImageId} could not be shown: {ex.Message}", stored.Folder);
            return new RecipeCardViewModel(stored with { Saved = null, ReadError = "recipe.json holds no recipe." }, now, OnSelectionChanged);
        }
    }

    private bool PassesStatus(RecipeCardViewModel c) => StatusFilter switch
    {
        RecipeStatusFilter.Ready => !c.IsBroken && c.IsReady,
        RecipeStatusFilter.Missing => !c.IsBroken && c.IsMissing,
        _ => true,
    };

    private bool PassesSearch(RecipeCardViewModel c) =>
        string.IsNullOrWhiteSpace(SearchText) || c.SearchText.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        var now = _nowLocal();
        var list = _cards.Where(c => c.IsKept == IsKeptView).ToList();
        var hiddenByOthers = list.Count(c => !PassesStatus(c) || !PassesSearch(c));
        var hiddenByNsfw = list.Count(c => !ShowNsfw && !c.IsSafe && PassesStatus(c) && PassesSearch(c));
        _visible = [.. list.Where(c => PassesStatus(c) && PassesSearch(c) && (ShowNsfw || c.IsSafe))];

        var where = IsKeptView ? "Kept" : "History";
        var hidden = hiddenByOthers + hiddenByNsfw;
        HiddenText = hidden == 0 ? null
            : hiddenByOthers == 0
                ? $"{(hidden == 1 ? "1 NSFW recipe" : $"{hidden} NSFW recipes")} hidden in {where}"
                : $"{Recipes(hidden)} hidden by filters in {where}";

        EmptyText = _visible.Count > 0 ? null
            : list.Count > 0 && !IsLibraryRead && StatusFilter != RecipeStatusFilter.All ? RecipeCardViewModel.CheckingText
            : list.Count == 0
                ? (IsKeptView
                    ? "Nothing kept yet. Use ★ on a recipe to keep it."
                    : "No recipes yet. Paste a Civitai image link into the search box in Browse Civitai and it lands here.")
                : $"No recipe matches. {HiddenText}";

        _quiet = true;
        foreach (var card in _cards.Where(c => c.IsSelected && !_visible.Contains(c))) card.IsSelected = false;
        _quiet = false;

        // Date order comes from the grouper; the flat order the selection ranges over follows it.
        var groups = RecipeDateGrouper.Group(_visible, c => c.PastedAt, now);
        Groups.Clear();
        foreach (var (header, items) in groups) Groups.Add(new RecipeGroupViewModel(header, items));
        _visible = [.. Groups.SelectMany(g => g.Cards)];

        UpdateSelectionTexts();
    }

    #endregion

    #region Selection

    private void OnSelectionChanged()
    {
        if (!_quiet) UpdateSelectionTexts();
    }

    /// <summary>Click selects one, Ctrl adds or removes, Shift selects the range from the last click.</summary>
    public void Select(RecipeCardViewModel card, bool additive, bool range)
    {
        _quiet = true;
        if (range && _anchor is not null && _visible.Contains(_anchor) && _visible.Contains(card))
        {
            if (!additive) foreach (var c in _cards) c.IsSelected = false;
            var from = _visible.IndexOf(_anchor);
            var to = _visible.IndexOf(card);
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) _visible[i].IsSelected = true;
        }
        else if (additive)
        {
            card.IsSelected = !card.IsSelected;
            _anchor = card;
        }
        else
        {
            foreach (var c in _cards) c.IsSelected = false;
            card.IsSelected = true;
            _anchor = card;
        }
        _quiet = false;
        UpdateSelectionTexts();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _quiet = true;
        foreach (var c in _cards) c.IsSelected = false;
        _quiet = false;
        UpdateSelectionTexts();
    }

    private List<RecipeCardViewModel> Selected() => [.. _cards.Where(c => c.IsSelected)];

    private void UpdateSelectionTexts()
    {
        var selected = Selected();
        SelectedCount = selected.Count;
        if (!IsLibraryRead)
        {
            // Against the empty index every LoRA would count as missing; the button claims no number yet.
            QueueButtonText = "Queue missing LoRAs";
            QueueTooltip = RecipeCardViewModel.CheckingText;
            return;
        }
        QueueButtonText = $"Queue missing LoRAs ({QueueCandidates(selected).Count})";

        // Each LoRA once across the selection, like the button: unlinked by hash or name, failed by version id.
        var installed = _host.Installed;
        var recipes = selected.Where(c => c.Stored.Saved is not null).Select(c => c.Stored.Saved!.Recipe).ToList();
        var notOnCivitai = recipes.SelectMany(r => r.UnlinkedLoras)
            .Where(u => !installed.HasFileHashPrefix(u.Hash))
            .Select(u => (u.Hash ?? u.Name).ToLowerInvariant()).Distinct().Count();
        var failed = recipes.SelectMany(r => r.Resources).Where(r => r.LoadError is not null).Select(r => r.VersionId).Distinct().Count();
        var parts = new List<string>();
        if (notOnCivitai > 0) parts.Add($"{notOnCivitai} not on Civitai");
        if (failed > 0) parts.Add($"{failed} could not be loaded");
        QueueTooltip = parts.Count > 0 ? string.Join(", ", parts) : QueueTooltipDefault;
    }

    /// <summary>The distinct LoRA versions the selected recipes miss and the browser can queue, by version id.</summary>
    private Dictionary<int, CivitaiModel> QueueCandidates(IEnumerable<RecipeCardViewModel> cards)
    {
        var installed = _host.Installed;
        var result = new Dictionary<int, CivitaiModel>();
        foreach (var resource in cards.Where(c => c.Stored.Saved is not null).SelectMany(c => c.Stored.Saved!.Recipe.Resources))
        {
            if (resource.LoadError is not null || resource.Model is null) continue;
            if (!CivitaiRecipeResourceRowViewModel.LoraTypes.Contains(resource.Model.Type)) continue;
            if (installed.IsInstalled(resource.Version)) continue;
            var versionId = resource.Version?.Id ?? resource.VersionId;
            if (!resource.Model.ModelVersions.Any(v => v.Id == versionId)) continue;
            result.TryAdd(versionId, resource.Model);
        }
        return result;
    }

    #endregion

    #region Opening a recipe

    public bool IsRecipeOpen => OpenRecipe is not null;

    public string BackText => IsKeptView ? "← Back to Kept" : "← Back to History";

    partial void OnIsKeptViewChanged(bool value)
    {
        OnPropertyChanged(nameof(BackText));
        Rebuild();
    }

    /// <summary>The background retry of the last opened recipe; tests await it.</summary>
    internal Task? RetryTask { get; private set; }

    private CivitaiRecipeViewModel BuildRecipeView(CivitaiImageRecipe recipe) =>
        new(recipe, _host.Installed, _host.CreateRecipeCard, _clipboard)
        {
            IsLibraryRead = IsLibraryRead,
            UrlOpener = _host.OpenUrl,
            EnqueueHandler = pairs =>
            {
                _logger?.Info(LogCategory.Download, LogSource,
                    $"Recipe {recipe.ImageId}: {pairs.Count} LoRA(s) sent to the queue: {string.Join(", ", pairs.Select(p => $"\"{p.Result.Name}\" {p.Pick.Name}"))}");
                _ = _host.EnqueueRecipeAsync(pairs);
            },
        };

    [RelayCommand]
    private void Open(RecipeCardViewModel? card)
    {
        if (card is null || card.IsBroken || card.Stored.Saved is not { } saved) return;

        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var ct = _openCts.Token;
        var previous = OpenRecipe;
        var view = BuildRecipeView(saved.Recipe);
        OpenRecipe = view;
        ReleaseCards(previous);
        _logger?.Info(LogCategory.General, LogSource, $"Recipe {card.ImageId} opened in the Recipes tab.");
        _ = ShowPreviewAsync(view, ct);

        var transient = saved.Recipe.Resources.Any(r => r.LoadError is not null && !r.IsDefiniteFailure);
        RetryTask = transient && _retryService is not null && _retried.Add(card.ImageId)
            ? RetryAsync(saved.Recipe, ct)
            : null;
    }

    /// <summary>Stops the in-flight preview work of the cards of a recipe view that is being dropped, as the browser does.</summary>
    private static void ReleaseCards(CivitaiRecipeViewModel? view)
    {
        if (view is null) return;
        foreach (var row in view.Rows) row.Card?.Cancel();
    }

    private async Task ShowPreviewAsync(CivitaiRecipeViewModel view, CancellationToken ct)
    {
        try { await view.LoadPreviewAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, $"Recipe {view.Recipe.ImageId}: the preview could not be loaded: {ex.Message}");
        }
        catch (OperationCanceledException) { /* the user went Back */ }
    }

    [RelayCommand]
    private void Back()
    {
        if (OpenRecipe is not { } open) return;
        _openCts?.Cancel();
        OpenRecipe = null;
        ReleaseCards(open);
        _logger?.Info(LogCategory.General, LogSource, $"Recipe {open.Recipe.ImageId}: back to {(IsKeptView ? "Kept" : "History")}.");
    }

    /// <summary>Loads the transiently failed resources again, once per recipe per session, and saves what loads.</summary>
    private async Task RetryAsync(CivitaiImageRecipe recipe, CancellationToken ct)
    {
        try
        {
            var apiKey = await _host.GetApiKeyAsync();
            // The save may finish after the user left; only the rebuild of the view depends on ct.
            var result = await _retryService!().RetryFailedResourcesAsync(recipe, apiKey, CancellationToken.None);
            if (ReferenceEquals(result, recipe)) return;

            StoredRecipe? stored;
            try
            {
                stored = await Task.Run(() => _store.Update(result));
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                _ui.Post(() => ReportStoreFailure(ex, $"Recipe {recipe.ImageId}: the retried resources were not saved."));
                return;
            }
            if (stored is null)
            {
                _logger?.Info(LogCategory.Network, LogSource, $"Recipe {recipe.ImageId}: deleted while its retry ran, nothing saved.");
                return;
            }
            var loaded = recipe.Resources.Count(r => r.LoadError is not null) - result.Resources.Count(r => r.LoadError is not null);
            _logger?.Info(LogCategory.Network, LogSource, $"Recipe {recipe.ImageId}: retry saved, {loaded} resource(s) loaded now.");

            // Judged by what is open now, not by the token of the open that started the retry: a reopen of
            // the same image during the retry was built from the old snapshot and needs this result.
            _ui.Post(() =>
            {
                if (_disposed || OpenRecipe?.Recipe.ImageId != recipe.ImageId) return;
                var old = OpenRecipe;
                var view = BuildRecipeView(result);
                OpenRecipe = view;
                ReleaseCards(old);
                _ = ShowPreviewAsync(view, _openCts?.Token ?? CancellationToken.None);
            });
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Network, LogSource, $"Recipe {recipe.ImageId}: retrying the failed resources failed: {ex.Message}", ex.ToString());
        }
    }

    #endregion

    #region Commands

    [RelayCommand] private void ShowHistory() => IsKeptView = false;

    [RelayCommand] private void ShowKept() => IsKeptView = true;

    [RelayCommand]
    private void ToggleKept(RecipeCardViewModel? card)
    {
        if (card is null || card.IsBroken) return;
        SetKept([card], !card.IsKept);
    }

    [RelayCommand] private void KeepSelected() => SetKept(Selected(), true);

    [RelayCommand] private void UnkeepSelected() => SetKept(Selected(), false);

    private void SetKept(List<RecipeCardViewModel> cards, bool kept)
    {
        var newer = cards.Count(c => c.Stored.IsNewerFormat);
        StatusMessage = newer == 0 ? null
            : $"{Recipes(newer)} made by a newer version of DiffusionNexus {(newer == 1 ? "was left as it is" : "were left as they are")}.";
        var ids = cards.Where(c => !c.IsBroken).Select(c => c.ImageId).ToList();
        if (ids.Count == 0) return;
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"{Recipes(ids.Count)} {(kept ? "kept" : "moved back to History")} from the Recipes tab.", string.Join(", ", ids));
        try
        {
            _store.SetKept(ids, kept);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            ReportStoreFailure(ex, string.Join(", ", ids));
        }
    }

    /// <summary>What a write to the store can meet: a locked file (antivirus, backup), no access, a newer version's file.</summary>
    private static bool IsStoreFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>A command that writes the store says why it could not, instead of throwing on the UI thread.</summary>
    private void ReportStoreFailure(Exception ex, string? detail)
    {
        StatusMessage = $"Could not change the recipe: {ex.Message}";
        _logger?.Warn(LogCategory.FileSystem, LogSource, StatusMessage, detail);
    }

    [RelayCommand(CanExecute = nameof(IsLibraryRead))]
    private async Task QueueMissingAsync()
    {
        var selected = Selected();
        var pairs = new List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>();
        foreach (var (versionId, model) in QueueCandidates(selected))
        {
            var card = _host.CreateRecipeCard(model);
            var pick = card.Versions.FirstOrDefault(v => v.Version.Id == versionId);
            if (pick is not null) pairs.Add((card, pick));
        }
        if (pairs.Count == 0) return;

        _logger?.Info(LogCategory.Download, LogSource,
            $"{pairs.Count} missing LoRA(s) from {Recipes(selected.Count)} handed to the download queue.",
            $"images {string.Join(", ", selected.Select(c => c.ImageId))}");
        await _host.EnqueueRecipeAsync(pairs);
    }

    [RelayCommand]
    private async Task OpenOnCivitaiAsync()
    {
        var urls = Selected().Where(c => c.Stored.Saved is not null).Select(c => c.Stored.Saved!.Recipe.CivitaiUrl).ToList();
        if (urls.Count == 0) return;
        if (urls.Count > OpenWithoutAsking)
        {
            if (_dialogs is null || !await _dialogs.ShowConfirmAsync("Open on Civitai", $"Open {urls.Count} pages in your browser?")) return;
        }
        foreach (var url in urls) _host.OpenUrl(url);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var size = FileSizeFormatter.Format(selected.Sum(c => c.Stored.SizeBytes));
        await DeleteAfterAskingAsync(selected, "Delete recipes",
            $"Delete {Recipes(selected.Count)}? This frees {size}. The images are removed from disk.");
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        // Broken folders are not part of History's clearing: the user has not seen what is in them.
        var history = _cards.Where(c => !c.IsKept && !c.IsBroken).ToList();
        if (history.Count == 0) return;
        var size = FileSizeFormatter.Format(history.Sum(c => c.Stored.SizeBytes));
        var broken = _cards.Count(c => !c.IsKept && c.IsBroken);
        var untouched = broken == 0
            ? "Kept recipes are not touched."
            : $"Kept recipes and {broken} unreadable {(broken == 1 ? "recipe" : "recipes")} are not touched.";
        // A recipe kept while the dialog was open must survive: judge each id again from the disk.
        await DeleteAfterAskingAsync(history, "Clear history",
            $"Delete all {Recipes(history.Count)} in History? This frees {size}. {untouched}",
            id => _store.TryLoad(id)?.Saved is { Kept: false });
    }

    private async Task DeleteAfterAskingAsync(List<RecipeCardViewModel> cards, string title, string message,
        Func<long, bool>? stillApplies = null)
    {
        if (_dialogs is null || !await _dialogs.ShowConfirmAsync(title, message)) return;
        var ids = cards.Select(c => c.ImageId).ToList();
        long freed = 0;
        IReadOnlyList<string> failures = [];
        try
        {
            await Task.Run(() =>
            {
                if (stillApplies is not null) ids = [.. ids.Where(stillApplies)];
                freed = _store.Delete(ids, out failures);
            });
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            ReportStoreFailure(ex, string.Join(", ", ids));
            return;
        }
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"{Recipes(ids.Count - failures.Count)} deleted from the Recipes tab ({title}), {FileSizeFormatter.Format(freed)} freed.",
            string.Join(", ", ids));
        // The store logged each failure; the user sees them here instead of a recipe that silently stays.
        StatusMessage = failures.Count == 0 ? null : $"Could not delete {Recipes(failures.Count)}: {failures[0]}";
    }

    #endregion
}
