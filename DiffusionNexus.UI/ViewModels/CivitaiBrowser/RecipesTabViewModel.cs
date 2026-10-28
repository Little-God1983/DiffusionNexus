using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;
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
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _queueButtonText = "Queue missing LoRAs (0)";
    [ObservableProperty] private string _queueTooltip = QueueTooltipDefault;

    private const string QueueTooltipDefault = "Queues the LoRAs these recipes are missing, each once.";

    public RecipesTabViewModel(RecipeStore store, IRecipeHost host, IDialogService? dialogs, IUnifiedLogger? logger,
        Func<DateTime>? nowLocal = null)
    {
        _store = store;
        _host = host;
        _dialogs = dialogs;
        _logger = logger;
        _nowLocal = nowLocal ?? (() => DateTime.Now);

        Reload();
        _store.Changed += OnStoreChanged;
        _host.InstalledIndexChanged += OnInstalledChanged;
    }

    public ObservableCollection<RecipeGroupViewModel> Groups { get; } = [];

    /// <summary>"1 recipe" / "3 recipes", the one wording every text of the tab uses.</summary>
    public static string Recipes(int count) => count == 1 ? "1 recipe" : $"{count} recipes";

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _host.InstalledIndexChanged -= OnInstalledChanged;
    }

    // The browser saves inside Task.Run, so Changed arrives on a pool thread.
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Reload();
        else Dispatcher.UIThread.Post(Reload);
    }

    private void OnInstalledChanged(object? sender, EventArgs e)
    {
        var installed = _host.Installed;
        foreach (var card in _cards) card.ApplyStatus(installed);
        Rebuild();
    }

    partial void OnIsKeptViewChanged(bool value) => Rebuild();
    partial void OnStatusFilterChanged(RecipeStatusFilter value) => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnShowNsfwChanged(bool value)
    {
        _logger?.Info(LogCategory.General, LogSource, $"Recipes tab: Show NSFW turned {(value ? "on" : "off")}.");
        Rebuild();
    }

    #region Loading and filtering

    private void Reload()
    {
        var now = _nowLocal();
        var installed = _host.Installed;
        var previous = _cards.ToDictionary(c => c.ImageId);
        var cards = new List<RecipeCardViewModel>();
        _quiet = true;
        foreach (var stored in _store.LoadAll())
        {
            var card = new RecipeCardViewModel(stored, now, OnSelectionChanged);
            card.ApplyStatus(installed);
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
        QueueButtonText = $"Queue missing LoRAs ({QueueCandidates(selected).Count})";

        var notOnCivitai = selected.Sum(c => c.Status?.MissingNotOnCivitai ?? 0);
        var failed = selected.Sum(c => c.Status?.FailedToLoad ?? 0);
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
        var ids = cards.Where(c => !c.IsBroken).Select(c => c.ImageId).ToList();
        if (ids.Count == 0) return;
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"{Recipes(ids.Count)} {(kept ? "kept" : "moved back to History")} from the Recipes tab.", string.Join(", ", ids));
        _store.SetKept(ids, kept);
    }

    [RelayCommand]
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
        await DeleteAfterAskingAsync(history, "Clear history",
            $"Delete all {Recipes(history.Count)} in History? This frees {size}. Kept recipes are not touched.");
    }

    private async Task DeleteAfterAskingAsync(List<RecipeCardViewModel> cards, string title, string message)
    {
        if (_dialogs is null || !await _dialogs.ShowConfirmAsync(title, message)) return;
        var ids = cards.Select(c => c.ImageId).ToList();
        var freed = await Task.Run(() => _store.Delete(ids));
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"{Recipes(ids.Count)} deleted from the Recipes tab ({title}), {FileSizeFormatter.Format(freed)} freed.",
            string.Join(", ", ids));
    }

    #endregion
}
