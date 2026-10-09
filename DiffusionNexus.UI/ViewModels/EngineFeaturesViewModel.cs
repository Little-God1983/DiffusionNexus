using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
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
    private CancellationTokenSource? _installCts;

    public EngineFeaturesViewModel(
        ICatalog catalog,
        IConfigurationCheckerService checker,
        IWorkloadInstallService installer,
        string engineRoot,
        IResourceMonitorService? resourceMonitor = null,
        IUnifiedLogger? unifiedLogger = null,
        EngineFeature? preselect = null,
        Func<string, long?>? freeSpaceProbe = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        _catalog = catalog;
        _checker = checker;
        _installer = installer;
        _engineRoot = engineRoot;
        _resourceMonitor = resourceMonitor;
        _unifiedLogger = unifiedLogger;
        _preselect = preselect;
        _freeSpaceProbe = freeSpaceProbe ?? ProbeFreeSpace;

        foreach (var definition in EngineFeatureCatalog.All)
        {
            var row = new EngineFeatureRowViewModel(definition);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(EngineFeatureRowViewModel.IsSelected) or nameof(EngineFeatureRowViewModel.Status))
                {
                    UpdateFooter();
                    InstallSelectedCommand.NotifyCanExecuteChanged();
                }
            };
            Rows.Add(row);
        }
    }

    public ObservableCollection<EngineFeatureRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private bool _isInstalling;

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

    private bool CanInstallSelected() => !IsInstalling && RowsToInstall.Any();

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedAsync()
    {
        IsInstalling = true;
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
                    var nodes = check.CustomNodeResults.Where(n => !n.IsInstalled).ToList();
                    var models = check.ModelResults.Where(m => !m.IsInstalled).ToList();
                    if (nodes.Count == 0 && models.Count == 0)
                        continue;

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
                            ProgressText = $"{row.DisplayName}: {p.ItemName} — {p.Message}";
                            if (p.IsFailed) Warn(ProgressText);
                            else Info(ProgressText);
                        }),
                        new Progress<DownloadProgress>(d =>
                        {
                            if (d.IsActive && !d.IsComplete)
                                ProgressText = $"{row.DisplayName}: downloading {d.FileName} {d.DownloadedSizeText} / {d.TotalSizeText} {d.SpeedText}";
                        }),
                        skipDownloadTokenProvider: null,
                        ct);

                    Info($"{row.DisplayName}: {summary}");
                    summaries.Add($"{row.DisplayName}: {summary.TrimEnd('.')}");
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
