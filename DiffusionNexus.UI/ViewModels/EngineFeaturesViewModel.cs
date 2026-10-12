using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.Engine;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// The Engine tile's Features dialog: tick app features, press Install selected, and the node packs
/// and models their catalog workloads declare are installed into the Engine — only what is missing.
/// </summary>
public sealed partial class EngineFeaturesViewModel : ViewModelBase
{
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineFeaturesViewModel>();

    private readonly ICatalog _catalog;
    private readonly IConfigurationCheckerService _checker;
    private readonly IWorkloadInstallService _installer;
    private readonly string _engineRoot;
    private readonly IResourceMonitorService? _resourceMonitor;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly EngineFeature? _preselect;
    private readonly Func<string, long?> _freeSpaceProbe;
    private readonly IInstallActivity? _installActivity;
    private readonly IDialogService? _dialogs;
    private readonly EngineRemover? _remover;
    private CancellationTokenSource? _installCts;
    private bool _acceptProgress;

    public EngineFeaturesViewModel(
        ICatalog catalog,
        IConfigurationCheckerService checker,
        IWorkloadInstallService installer,
        string engineRoot,
        IResourceMonitorService? resourceMonitor = null,
        IUnifiedLogger? unifiedLogger = null,
        EngineFeature? preselect = null,
        Func<string, long?>? freeSpaceProbe = null,
        IInstallActivity? installActivity = null,
        IDialogService? dialogs = null,
        EngineRemover? remover = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        _installActivity = installActivity;
        _catalog = catalog;
        _checker = checker;
        _installer = installer;
        _engineRoot = engineRoot;
        _resourceMonitor = resourceMonitor;
        _unifiedLogger = unifiedLogger;
        _preselect = preselect;
        _freeSpaceProbe = freeSpaceProbe ?? ProbeFreeSpace;
        _dialogs = dialogs;
        _remover = remover;

        foreach (var definition in EngineFeatureCatalog.All)
        {
            var row = new EngineFeatureRowViewModel(definition);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(EngineFeatureRowViewModel.IsSelected) or nameof(EngineFeatureRowViewModel.Status))
                {
                    UpdateFooter();
                    InstallSelectedCommand.NotifyCanExecuteChanged();
                    RemoveFeatureCommand.NotifyCanExecuteChanged();
                }
            };
            Rows.Add(row);
        }
    }

    public ObservableCollection<EngineFeatureRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFeatureCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFeatureCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isRemoving;

    /// <summary>An install or a removal runs: the dialog keeps Close disabled and shows the progress bar.</summary>
    public bool IsBusy => IsInstalling || IsRemoving;

    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string _footerText = string.Empty;

    /// <summary>
    /// True once an install started, so the caller can re-sync model paths, restart the Engine and
    /// refresh readiness. Set before the install call, because one that throws after a partial
    /// download has still changed the disk. Internal setter is a test seam.
    /// </summary>
    public bool DidInstall { get; internal set; }

    /// <summary>
    /// True once an install call was made with at least one missing node pack. Only then must a
    /// running Engine restart: ComfyUI loads custom nodes at start-up but finds new model files on
    /// its own. Internal setter is a test seam.
    /// </summary>
    public bool DidInstallNodePacks { get; internal set; }

    /// <summary>True once a removal deleted something, so the caller tells open panels to re-check. Internal setter is a test seam.</summary>
    public bool DidRemove { get; internal set; }

    private IEnumerable<EngineFeatureRowViewModel> RowsToInstall =>
        Rows.Where(r => r.IsSelected && r.IsSelectable);

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await CheckAllAsync(CancellationToken.None);
            if (_preselect is { } pre)
            {
                var row = Rows.First(r => r.Definition.Feature == pre);
                if (row.IsSelectable) row.IsSelected = true;
            }
        }
        finally
        {
            IsLoading = false;
            UpdateFooter();
        }
    }

    private bool CanInstallSelected() => !IsInstalling && !IsRemoving && RowsToInstall.Any();

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedAsync()
    {
        IsInstalling = true;
        // The catalog apply waits while this runs (#618); ends with the method, after IsInstalling is cleared.
        using var running = _installActivity?.Begin("Diffusion Nexus Engine features");
        _acceptProgress = true;
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;
        var rows = RowsToInstall.ToList();
        var summaries = new List<string>();
        var completed = false;
        Info($"Installing {string.Join(", ", rows.Select(r => r.DisplayName))} into {_engineRoot}.");

        try
        {
            foreach (var row in rows)
            {
                row.Status = EngineFeatureStatus.Installing;
                row.StatusText = "Installing…";

                foreach (var workloadId in row.Definition.WorkloadIds)
                {
                    var config = await _catalog.GetWorkloadAsync(workloadId, ct)
                        ?? throw new InvalidOperationException($"Workload {workloadId} is missing from the catalog.");

                    // Fresh check right before installing: a file shared with an earlier workload in
                    // this run, or delivered since the dialog opened, must not be downloaded again.
                    var check = await _checker.CheckConfigurationAsync(config, _engineRoot, options: null, ct);
                    IReadOnlyList<Services.ConfigurationChecker.Models.CustomNodeCheckResult> nodes = check.CustomNodeResults.Where(n => !n.IsInstalled).ToList();
                    var models = check.ModelResults.Where(m => !m.IsInstalled).ToList();
                    if (nodes.Count == 0 && models.Count == 0)
                        continue;

                    // No node pack without its wheel: the pack's requirements would make pip compile
                    // llama-cpp-python, and a pack on disk reads as installed, so Install would never retry.
                    var wheelNote = "";
                    if (nodes.Count > 0 && config.InstallLamaCpp
                        && await EnsureLlamaCppWheelAsync(row, ct) == LlamaCppWheelOutcome.Failed)
                    {
                        Warn($"{row.DisplayName}: llama-cpp-python was not installed; the node packs wait for it, Install again to retry.");
                        wheelNote = "llama-cpp-python was not installed; ";
                        nodes = [];
                        if (models.Count == 0)
                        {
                            summaries.Add($"{row.DisplayName}: llama-cpp-python was not installed");
                            continue;
                        }
                    }

                    var vramGb = await SuggestVramAsync(config.Vram.VramProfiles, ct);
                    ProgressText = $"{row.DisplayName}: installing {nodes.Count} node pack(s) and {models.Count} model(s)…";
                    Info(ProgressText);

                    DidInstall = true;
                    if (nodes.Count > 0)
                        DidInstallNodePacks = true;
                    var summary = await _installer.InstallSelectedAsync(
                        config, _engineRoot, nodes, models, vramGb,
                        new Progress<WorkloadInstallProgress>(p =>
                        {
                            if (!_acceptProgress) return;
                            ProgressText = $"{row.DisplayName}: {p.ItemName} — {p.Message}";
                            if (p.IsFailed) Warn(ProgressText);
                            else Info(ProgressText);
                        }),
                        new Progress<DownloadProgress>(d =>
                        {
                            if (!_acceptProgress) return;
                            if (d.IsActive && !d.IsComplete)
                                ProgressText = $"{row.DisplayName}: downloading {d.FileName} {d.DownloadedSizeText} / {d.TotalSizeText} {d.SpeedText}";
                        }),
                        skipDownloadTokenProvider: null,
                        modelsRootOverride: ManagedEngineLocator.ModelsFolder(_engineRoot),
                        ct);

                    Info($"{row.DisplayName}: {summary}");
                    summaries.Add($"{row.DisplayName}: {wheelNote}{summary.TrimEnd('.')}");
                }
            }

            completed = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Install cancelled.";
            Info(ProgressText);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Engine feature install failed");
            _unifiedLogger?.Error(LogCategory.Installation, LogSource, "Feature install failed", ex);
            ProgressText = $"Install failed: {ex.Message}";
        }
        finally
        {
            // Progress<T> posts to the UI thread; a report the installer sent just before returning can
            // arrive after the outcome is written and replace "Done." with the last item's line.
            _acceptProgress = false;
            try
            {
                await CheckAllAsync(CancellationToken.None);
                if (completed)
                    ReportOutcome(rows, summaries);
            }
            catch (Exception ex)
            {
                // The re-check must never keep the dialog locked: IsInstalling is reset below regardless.
                Logger.Warning(ex, "Engine feature re-check after install failed");
                _unifiedLogger?.Warn(LogCategory.Installation, LogSource, $"Re-check after install failed — {ex.Message}");
                // Rows the re-check did not reach are not Installed, so this reports problems.
                if (completed)
                    ReportOutcome(rows, summaries);
            }
            finally
            {
                _installCts?.Dispose();
                _installCts = null;
                IsInstalling = false;
            }
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    private bool CanRemoveFeature(EngineFeatureRowViewModel? row) =>
        row is { CanRemove: true } && !IsInstalling && !IsRemoving && _remover is not null && _dialogs is not null;

    /// <summary>
    /// Remove…: plans what goes (node packs and models no other installed feature uses), asks — with the
    /// user's own folders' copies listed and optional — then stops the Engine and deletes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveFeature))]
    private async Task RemoveFeatureAsync(EngineFeatureRowViewModel row)
    {
        if (_remover is null || _dialogs is null) return;
        if (_remover.BusyReason() is { } busy)
        {
            Info($"{row.DisplayName}: removal refused, the Engine is busy ({busy}). Nothing was changed.");
            await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle, EngineRemovalMessages.Busy(busy));
            return;
        }

        IsRemoving = true;
        try
        {
            EngineRemovalPlan plan;
            try
            {
                var checks = await EngineFeatureChecks.LoadAsync(_catalog, _checker, _engineRoot, CancellationToken.None);
                plan = await Task.Run(() => EngineRemovalPlanner.PlanFeatureRemoval(_engineRoot, checks, row.Definition.Feature));
            }
            catch (Exception ex)
            {
                // Runtime checks fall through, never block: without a plan nothing is deleted, the dialog stays usable.
                Warn($"{row.DisplayName}: the feature's files could not be checked — {ex.Message}. Nothing was removed.", ex);
                await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle,
                    EngineRemovalMessages.CheckFailed(row.DisplayName, ex.Message));
                return;
            }

            Info($"{row.DisplayName}: remove plan — {plan.NodePackFolders.Count} node pack(s), {plan.EngineModels.Count} Engine model(s) " +
                 $"({EngineRemovalMessages.FormatSize(plan.EngineBytes)}), {plan.OutsideModels.Count} in your own folders, " +
                 $"{plan.Kept.Count} kept ({string.Join(", ", plan.Kept.Select(k => $"{k.Name} for {k.NeededBy}"))})" +
                 (plan.RemovedWithIt.Count > 0
                     ? $"; goes with it, using only its files: {string.Join(", ", plan.RemovedWithIt)}."
                     : "."));

            if (!plan.HasAnythingToDelete)
            {
                await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle, EngineRemovalMessages.NothingToRemove(row.DisplayName, plan));
                return;
            }

            var hasOutside = plan.OutsideModels.Count > 0;
            var choice = EngineRemovalMessages.ToChoice(
                await _dialogs.ShowOptionsAsync(EngineRemovalMessages.FeatureTitle,
                    EngineRemovalMessages.FeatureRemoval(row.DisplayName, plan), EngineRemovalMessages.FeatureOptions(hasOutside)),
                hasOutside);
            if (choice == RemovalChoice.Cancel)
            {
                Info($"{row.DisplayName}: removal cancelled. Nothing was changed.");
                return;
            }

            // Only the user's own copies are left, and the user chose to keep them: nothing to delete.
            if (choice == RemovalChoice.EngineOnly && plan.NodePackFolders.Count == 0 && plan.EngineModels.Count == 0)
            {
                ProgressText = $"Nothing of {row.DisplayName} is in the Engine; your own files were left as they are.";
                Info(ProgressText);
                return;
            }

            var files = plan.EngineModels.Select(f => f.Path)
                .Concat(choice == RemovalChoice.Everything ? plan.OutsideModels.Select(f => f.Path) : [])
                .ToList();
            Info($"{row.DisplayName}: removing ({(choice == RemovalChoice.Everything ? "your own folders' copies too" : "only what's in the Engine")}).");
            ProgressText = $"Removing {row.DisplayName}…";

            var result = await _remover.RemoveAsync(plan.NodePackFolders, files, engineRoot: _engineRoot);
            if (result.WasBusy)
            {
                ProgressText = null;
                await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle, EngineRemovalMessages.Busy(result.BusyReason!));
                return;
            }

            DidRemove = true;
            if (result.Failed.Count == 0)
            {
                ProgressText = $"Removed {row.DisplayName}.";
                Info(ProgressText);
            }
            else
            {
                ProgressText = $"Removed {row.DisplayName} with problems: {result.Failed.Count} item(s) could not be deleted. See the Unified Console.";
                Warn(ProgressText);
                await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle,
                    EngineRemovalMessages.Failures($"{row.DisplayName} was not removed completely.", result.Failed));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Engine feature removal failed");
            _unifiedLogger?.Error(LogCategory.Installation, LogSource, "Feature removal failed", ex);
            ProgressText = $"Remove failed: {ex.Message}";
        }
        finally
        {
            try { await CheckAllAsync(CancellationToken.None); }
            catch (Exception ex) { Warn($"Re-check after removal failed — {ex.Message}", ex); }
            IsRemoving = false;
        }
    }

    /// <summary>
    /// A workload whose node packs run GGUF models (Outpaint Vision's Qwen3-VL node) gets the prebuilt
    /// llama-cpp-python wheel first, so pip finds the node's requirement satisfied instead of compiling
    /// it from source. The service picks the wheel for the Engine venv's own Python and CUDA.
    /// </summary>
    private async Task<LlamaCppWheelOutcome> EnsureLlamaCppWheelAsync(EngineFeatureRowViewModel row, CancellationToken ct)
    {
        ProgressText = $"{row.DisplayName}: checking llama-cpp-python…";
        Info(ProgressText);
        var outcome = await _installer.EnsureLlamaCppWheelAsync(_engineRoot, await _catalog.GetLamaCppWheelsAsync(ct),
            new Progress<WorkloadInstallProgress>(p =>
            {
                if (!_acceptProgress) return;
                ProgressText = $"{row.DisplayName}: {p.Message}";
                if (p.IsFailed) Warn(ProgressText);
                else Info(ProgressText);
            }), ct);
        return outcome;
    }

    /// <summary>
    /// The installer reports failed items in its summary rather than throwing, so "Done." is only
    /// honest when the re-check finds every selected row installed.
    /// </summary>
    private void ReportOutcome(IReadOnlyList<EngineFeatureRowViewModel> rows, IReadOnlyList<string> summaries)
    {
        var summaryText = string.Join("; ", summaries);
        if (rows.Any(r => r.Status != EngineFeatureStatus.Installed))
        {
            ProgressText = summaryText.Length > 0
                ? $"Finished with problems: {summaryText}. See the Unified Console for details."
                : "Finished with problems. See the Unified Console for details.";
            Warn(ProgressText);
        }
        else
        {
            ProgressText = summaryText.Length > 0 ? $"Done. {summaryText}." : "Done.";
            Info(ProgressText);
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var row in Rows)
        {
            row.Checks.Clear();
            try
            {
                foreach (var workloadId in row.Definition.WorkloadIds)
                {
                    var config = await _catalog.GetWorkloadAsync(workloadId, ct);
                    if (config is null)
                        throw new InvalidOperationException($"Workload {workloadId} is missing from the catalog.");
                    row.Checks.Add((config, await _checker.CheckConfigurationAsync(config, _engineRoot, options: null, ct)));
                }

                row.ApplyChecks();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"{row.DisplayName}: check failed — {ex.Message}", ex);
                row.Status = EngineFeatureStatus.Error;
                row.StatusText = "Check failed";
            }
        }
    }

    private async Task<int> SuggestVramAsync(string? vramProfiles, CancellationToken ct)
    {
        if (_resourceMonitor is null) return 0;
        var snapshot = await _resourceMonitor.GetSnapshotAsync(ct);
        return EngineFeatureCatalog.SuggestVramTier(snapshot.VramTotalMB, EngineFeatureCatalog.ParseVramProfiles(vramProfiles));
    }

    private void UpdateFooter()
    {
        var count = RowsToInstall.Count();
        var drive = Path.GetPathRoot(_engineRoot) ?? _engineRoot;
        var free = _freeSpaceProbe(_engineRoot);
        var freeText = free is { } bytes ? $" · {bytes / (1024L * 1024 * 1024)} GB free on {drive}" : string.Empty;
        FooterText = $"Selected: {count} feature{(count == 1 ? "" : "s")}{freeText}";
    }

    private static long? ProbeFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch { return null; }
    }

    private void Warn(string message, Exception? ex = null)
    {
        Logger.Warning(ex, "Engine features: {Message}", message);
        _unifiedLogger?.Warn(LogCategory.Installation, LogSource, message);
    }

    private void Info(string message)
    {
        Logger.Information("Engine features: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Installation, LogSource, message);
    }
}
