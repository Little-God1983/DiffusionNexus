using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Catalog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One "Added" / "Updated" / "Removed" block of the change list.</summary>
public sealed record CatalogChangeGroup(string Heading, IReadOnlyList<string> Rows);

/// <summary>
/// Settings → Catalog (#618): renders <see cref="ICatalogUpdateCoordinator"/>, mirroring the installer's
/// update page. Everything is recomputed from the coordinator on each Changed, on the UI thread.
/// </summary>
public sealed partial class CatalogSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ICatalogUpdateCoordinator _catalog;
    private readonly IUiScheduler _ui;
    private bool _refreshing;

    public CatalogSettingsViewModel(ICatalogUpdateCoordinator catalog, IUiScheduler ui)
    {
        _catalog = catalog;
        _ui = ui;
        _catalog.Changed += OnChanged;
        Refresh();
    }

    [ObservableProperty] private bool _isStable;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private bool _canChooseChannel;
    [ObservableProperty] private string? _channelHint;
    [ObservableProperty] private bool _autoApply;
    [ObservableProperty] private bool _canToggleAutoApply;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _outcomeText = string.Empty;
    [ObservableProperty] private string? _lastCheckedText;
    [ObservableProperty] private IReadOnlyList<CatalogChangeGroup> _changeGroups = [];
    [ObservableProperty] private bool _showApply;
    [ObservableProperty] private string? _applyBlockedReason;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _showWarning;
    [ObservableProperty] private string? _warningText;
    [ObservableProperty] private string _switchButtonText = string.Empty;
    [ObservableProperty] private string _keepButtonText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    partial void OnIsStableChanged(bool value) { if (value && !_refreshing) _ = SwitchAsync(CatalogChannel.Stable); }
    partial void OnIsPreviewChanged(bool value) { if (value && !_refreshing) _ = SwitchAsync(CatalogChannel.Preview); }
    partial void OnAutoApplyChanged(bool value) { if (!_refreshing) _ = SetAutoApplyAsync(value); }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => _catalog.CheckAsync();
    private bool CanCheck() => !IsBusy && !ShowWarning;

    [RelayCommand(CanExecute = nameof(CanApplyNow))]
    private Task ApplyAsync() => _catalog.ApplyAsync();
    private bool CanApplyNow() => _catalog.CanApply;

    [RelayCommand]
    private Task ConfirmSwitchAsync() => _catalog.ConfirmSwitchAsync();

    [RelayCommand]
    private void KeepChannel() => _catalog.KeepChannel();

    private async Task SwitchAsync(CatalogChannel target)
    {
        try
        {
            await _catalog.SwitchChannelAsync(target);
        }
        catch (Exception ex)
        {
            ErrorText = $"The channel could not be saved: {ex.Message}";
        }
        Refresh();   // a refused switch changes nothing in the coordinator; put the dot back
    }

    private async Task SetAutoApplyAsync(bool on)
    {
        try
        {
            if (!await _catalog.SetAutoApplyAsync(on)) Refresh();   // refused: snap back
        }
        catch (Exception ex)
        {
            ErrorText = $"The setting could not be saved: {ex.Message}";
            Refresh();
        }
    }

    private void OnChanged() => _ui.Post(Refresh);

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var shown = _catalog.PendingSwitch?.Target ?? _catalog.Channel;
            IsStable = shown == CatalogChannel.Stable;
            IsPreview = shown == CatalogChannel.Preview;
            var busy = _catalog.Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying;
            IsBusy = busy;
            var pinned = _catalog.ChannelSource == CatalogChannelSource.Environment;
            CanChooseChannel = !pinned && !busy && _catalog.PendingSwitch is null;
            ChannelHint = pinned ? "Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run." : null;
            AutoApply = _catalog.AutoApply;
            CanToggleAutoApply = !busy && _catalog.PendingSwitch is null;

            StatusText = Status(_catalog);
            OutcomeText = Outcome(_catalog);
            LastCheckedText = _catalog.LastCheckedAt is { } at ? "Last checked " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : null;

            var showList = _catalog.LastCheck is { } check && _catalog.Phase != CatalogUpdatePhase.Checking
                && (_catalog.UpdateAvailable || (_catalog.Phase == CatalogUpdatePhase.Applied && _catalog.LastApplyAutomatic));
            ChangeGroups = showList ? Groups(_catalog.LastCheck!) : [];
            ShowApply = _catalog.UpdateAvailable && _catalog.Phase != CatalogUpdatePhase.Applying && _catalog.ApplyBlockedReason is null;
            ApplyBlockedReason = _catalog.Phase != CatalogUpdatePhase.Applying ? _catalog.ApplyBlockedReason : null;
            ProgressText = _catalog.Phase == CatalogUpdatePhase.Applying ? Progress(_catalog.Progress) : null;
            if (_catalog.Phase != CatalogUpdatePhase.Applying && _catalog.LastApplyFailed && _catalog.LastApply is { } failed)
                ErrorText = $"The catalog update failed: {(failed.Error ?? $"{failed.Failed} not applied").TrimEnd('.')}.";
            else if (busy || _catalog.LastApply is null)
                ErrorText = null;

            if (_catalog.PendingSwitch is { } pending)
            {
                ShowWarning = true;
                WarningText = Warning(pending.Warning);
                SwitchButtonText = $"Switch to {pending.Target}";
                KeepButtonText = $"Keep {_catalog.Channel}";
            }
            else
            {
                ShowWarning = false;
                WarningText = null;
            }
        }
        finally
        {
            _refreshing = false;
        }
        CheckCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private static string Status(ICatalogUpdateCoordinator c)
    {
        if (c.Installed is not { } s || !s.RecordsAnything()) return $"No catalog installed yet · {c.Channel}";
        var text = $"Catalog v{s.HighestCatalogVersion} · {c.Channel}";
        return (s.Workloads?.Channel ?? s.Workflows?.Channel) is { } recorded && recorded != c.Channel ? $"{text} (content from {recorded})" : text;
    }

    private static string Outcome(ICatalogUpdateCoordinator c)
    {
        if (c.PendingSwitch is { } pending) return $"Switching to {pending.Target} waits for your answer below.";
        if (c.Phase == CatalogUpdatePhase.Checking) return "Checking the catalog…";
        if (c.Phase == CatalogUpdatePhase.Applied)
        {
            var landed = c.Installed is { } s && s.RecordsAnything() ? s.HighestCatalogVersion : c.LastCheck?.Remote?.CatalogVersion;
            if (!c.LastApplyAutomatic) return $"Catalog updated to v{landed}.";
            return c.LastCheck is { HasChanges: false }
                ? $"Catalog v{landed} applied automatically; no changes for Diffusion Nexus."
                : $"Catalog updated to v{landed} automatically.";
        }
        return c.LastCheck switch
        {
            null => "Not checked yet.",
            { IsSwitchPreview: true } k when c.UpdateAvailable => $"Catalog update available – v{k.Remote?.CatalogVersion}",
            { Outcome: CatalogUpdateOutcome.UpToDate } => "Up to date.",
            { Outcome: CatalogUpdateOutcome.UpdatesAvailable } k when !c.UpdateAvailable && c.Phase != CatalogUpdatePhase.Applying
                => $"Catalog v{k.Remote?.CatalogVersion} has no changes for Diffusion Nexus.",
            { Outcome: CatalogUpdateOutcome.UpdatesAvailable } k => $"Catalog update available – v{k.Remote?.CatalogVersion}",
            { Outcome: CatalogUpdateOutcome.RequiresNewerSoftware } => "This catalog update needs a newer version of Diffusion Nexus.",
            { Outcome: CatalogUpdateOutcome.OverrideActive } => $"Update check skipped: a local catalog override is active at {c.OverridePath}.",
            { Outcome: CatalogUpdateOutcome.Failed } k => $"The catalog check failed: {k.Error}",
            _ => string.Empty,
        };
    }

    private static string Progress(CatalogDownloadProgress? p) => p switch
    {
        null => "Applying the catalog…",
        { TotalBytes: > 0 } t => $"Downloading… {(100.0 * t.BytesReceived / t.TotalBytes.Value).ToString("F0", CultureInfo.InvariantCulture)}%",
        var t => $"Downloading… {(t.BytesReceived / 1_048_576.0).ToString("F1", CultureInfo.InvariantCulture)} MB",
    };

    private static string Warning(ChannelSwitchWarning w)
    {
        var parts = new List<string>();
        if (w.Removed.Count > 0) parts.Add("removes " + Join(w.Removed));
        if (w.ChangesText is { } changes) parts.Add(changes);
        return $"{w.Target} is at v{w.Version}. Switching {string.Join(" and ", parts)}. Software you have already installed is not affected.";
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static IReadOnlyList<CatalogChangeGroup> Groups(CatalogUpdateCheck check)
    {
        var rows = CatalogChangeRows.Build(check);
        return new[] { ChangeKind.Added, ChangeKind.Updated, ChangeKind.Removed }
            .Select(kind => new CatalogChangeGroup(kind.ToString(),
                rows.Where(r => r.Change == kind).Select(r => string.IsNullOrEmpty(r.VersionText) ? $"{r.Kind} · {r.Name}" : $"{r.Kind} · {r.Name} · {r.VersionText}").ToList()))
            .Where(g => g.Rows.Count > 0)
            .ToList();
    }

    public void Dispose() => _catalog.Changed -= OnChanged;
}
