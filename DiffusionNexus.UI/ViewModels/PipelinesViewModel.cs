using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Pipelines;
using DiffusionNexus.UI.ViewModels.Pipelines;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Pipelines module — a tile gallery of guided image pipelines. The tile is a
/// launcher + readiness indicator; the actual model downloading is handled centrally in the
/// Installer Manager → Workloads dialog (Diffusion Nexus tab). Clicking a tile re-checks readiness
/// and, when assets are missing, points the user to that dialog.
/// </summary>
public partial class PipelinesViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<PipelinesViewModel>();

    private readonly IPipelineManifestProvider? _manifestProvider;
    private readonly IPipelineAssetInstaller? _installer;
    private readonly IDialogService? _dialogService;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly Func<PipelineTileViewModel, ViewModels.Pipelines.IPipelineRun>? _runFactory;

    /// <summary>The pipeline tiles displayed in the gallery.</summary>
    public ObservableCollection<PipelineTileViewModel> Pipelines { get; } = new();

    /// <summary>Generation-category tiles (shown above the Utilities divider).</summary>
    public IEnumerable<PipelineTileViewModel> GenerationPipelines => Pipelines.Where(t => !IsUtility(t));

    /// <summary>Utility-category tiles (shown below the Utilities divider).</summary>
    public IEnumerable<PipelineTileViewModel> UtilityPipelines => Pipelines.Where(IsUtility);

    /// <summary>True when at least one utility tile exists (drives the divider's visibility).</summary>
    public bool HasUtilityPipelines => Pipelines.Any(IsUtility);

    private static bool IsUtility(PipelineTileViewModel t) =>
        string.Equals(t.Manifest.Category, "Utilities", StringComparison.OrdinalIgnoreCase);

    /// <summary>GPU/RAM monitor widget shown atop the gallery (null at design time).</summary>
    public ResourceMonitorViewModel? ResourceMonitor { get; }

    /// <summary>
    /// The pipeline currently being run (its run UI replaces the gallery). Null = showing the gallery.
    /// </summary>
    [ObservableProperty]
    private ViewModels.Pipelines.IPipelineRun? _activeRun;

    /// <summary>Design-time constructor (also used as a safe fallback).</summary>
    public PipelinesViewModel()
    {
        Pipelines.Add(new PipelineTileViewModel());
    }

    public PipelinesViewModel(
        IPipelineManifestProvider manifestProvider,
        IPipelineAssetInstaller installer,
        ResourceMonitorViewModel? resourceMonitor = null,
        Func<PipelineTileViewModel, ViewModels.Pipelines.IPipelineRun>? runFactory = null,
        IDialogService? dialogService = null,
        IUnifiedLogger? unifiedLogger = null)
    {
        _manifestProvider = manifestProvider ?? throw new ArgumentNullException(nameof(manifestProvider));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        ResourceMonitor = resourceMonitor;
        _runFactory = runFactory;
        _dialogService = dialogService;
        _unifiedLogger = unifiedLogger;

        // Only guided pipelines with a run screen get a gallery tile. Download-only model bundles
        // (e.g. Qwen-Image-2512, used directly in the Diffusion Canvas) set ShowInGallery=false; they
        // are still installable from Installer Manager → Diffusion Nexus Core → Workloads.
        foreach (var manifest in _manifestProvider.All().Where(m => m.ShowInGallery))
            Pipelines.Add(new PipelineTileViewModel(manifest));

        // Compute initial readiness badges without blocking construction.
        _ = RefreshAllStatusesAsync();
    }

    /// <summary>Re-checks every tile's on-disk readiness and updates its badge.</summary>
    public async Task RefreshAllStatusesAsync()
    {
        if (_installer is null)
            return;

        string? root;
        try
        {
            root = await _installer.ResolveModelsRootAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to resolve ComfyUI models root for pipeline readiness.");
            root = null;
        }

        foreach (var tile in Pipelines)
        {
            if (!tile.Manifest.RequiresModels)
            {
                SetStatus(tile, PipelineStatus.Ready, "Ready");
                continue;
            }

            if (root is null)
            {
                SetStatus(tile, PipelineStatus.NotInstalled, "No ComfyUI install");
                continue;
            }

            try
            {
                var readiness = await _installer.CheckAsync(tile.Manifest).ConfigureAwait(true);
                if (readiness.IsComplete)
                    SetStatus(tile, PipelineStatus.Ready, "Ready");
                else
                    SetStatus(tile, PipelineStatus.NotInstalled, $"{readiness.Missing.Count} missing");
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Readiness check failed for pipeline {Id}.", tile.Id);
                SetStatus(tile, PipelineStatus.Error, "Check failed");
            }
        }
    }

    [RelayCommand]
    private Task OpenPipelineAsync(PipelineTileViewModel? tile) => OpenPipelineInternalAsync(tile, null);

    /// <summary>
    /// Opens a workflow's run screen by manifest id, pre-loaded with <paramref name="inputImages"/>.
    /// Drives the Generation Gallery / reusable "Send to → Workflows" flow: the host has already
    /// switched to this module; here we locate the tile, run the same readiness gate as a tile click,
    /// and hand the images to the run once it opens.
    /// </summary>
    public async Task OpenWorkflowAsync(string workflowId, IReadOnlyList<string>? inputImages)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            return;

        var tile = Pipelines.FirstOrDefault(
            t => string.Equals(t.Id, workflowId, StringComparison.OrdinalIgnoreCase));
        if (tile is null)
        {
            await ShowMessageAsync("Workflow unavailable",
                $"The '{workflowId}' workflow isn't available. It may have been removed or hidden.");
            return;
        }

        // Sending from one run's results into another workflow would otherwise leak the old run VM
        // (OpenRun overwrites ActiveRun without disposing); close it first.
        if (ActiveRun is not null)
            CloseRun();

        await OpenPipelineInternalAsync(tile, inputImages);
    }

    private async Task OpenPipelineInternalAsync(PipelineTileViewModel? tile, IReadOnlyList<string>? inputImages)
    {
        if (tile is null)
            return;

        Logger.Information("Pipeline tile opened: {PipelineId}", tile.Id);

        if (_installer is null || _manifestProvider is null)
            return; // design-time / not wired

        if (tile.IsBusy)
            return;

        // Utility workflows (e.g. the metadata distiller) need no ComfyUI models — open directly.
        if (!tile.Manifest.RequiresModels)
        {
            OpenRun(tile, inputImages);
            return;
        }

        try
        {
            // The tile is a launcher + readiness indicator only. All model downloading is handled
            // centrally in the Installer Manager → Workloads dialog (Diffusion Nexus tab), so here
            // we just refresh status and point the user there when assets are missing.
            var root = await _installer.ResolveModelsRootAsync();
            if (string.IsNullOrEmpty(root))
            {
                await ShowMessageAsync("No ComfyUI installation",
                    "Pipeline models live in a ComfyUI installation's models folder (which the local renderer also reads).\n\n" +
                    "Add a ComfyUI installation in the Installer Manager first, then try again.");
                SetStatus(tile, PipelineStatus.NotInstalled, "No ComfyUI install");
                return;
            }

            var readiness = await _installer.CheckAsync(tile.Manifest);
            if (readiness.IsComplete)
            {
                SetStatus(tile, PipelineStatus.Ready, "Ready");
                OpenRun(tile, inputImages);
                return;
            }

            SetStatus(tile, PipelineStatus.NotInstalled, $"{readiness.Missing.Count} missing");
            var missingNames = string.Join("\n", readiness.Missing.Select(m => $"  • {m.Name}"));
            await ShowMessageAsync(tile.Title,
                $"This pipeline still needs {readiness.Missing.Count} asset(s):\n\n{missingNames}\n\n" +
                "Install them from the Installer Manager → Diffusion Nexus Core → Workloads → " +
                $"the \"Pipelines\" tab → \"{tile.Title}\" → Details.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Pipeline open failed for {Id}.", tile.Id);
            SetStatus(tile, PipelineStatus.Error, "Failed");
            await ShowMessageAsync("Error", ex.Message);
        }
    }

    /// <summary>
    /// Opens the pipeline's run UI (replaces the gallery) via the registered factory. When
    /// <paramref name="inputImages"/> is supplied (the "Send to → Workflows" flow), the run starts in
    /// loose-image mode pre-loaded with those images.
    /// </summary>
    private void OpenRun(PipelineTileViewModel tile, IReadOnlyList<string>? inputImages = null)
    {
        if (_runFactory is null)
            return;

        try
        {
            var run = _runFactory(tile);
            run.ResourceMonitor = ResourceMonitor; // reuse the gallery's single nvidia-smi poller
            run.CloseRequested += OnRunCloseRequested;
            if (inputImages is { Count: > 0 })
                run.LoadInputImages(inputImages);
            ActiveRun = run;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open run UI for pipeline {Id}.", tile.Id);
            _ = ShowMessageAsync("Error", $"Could not open '{tile.Title}': {ex.Message}");
        }
    }

    private void OnRunCloseRequested(object? sender, EventArgs e) => CloseRun();

    [RelayCommand]
    private void CloseRun()
    {
        if (ActiveRun is { } run)
        {
            run.CloseRequested -= OnRunCloseRequested;
            run.Dispose();
            ActiveRun = null;
        }

        // A run may have created a new dataset version / written outputs; refresh the badges.
        _ = RefreshAllStatusesAsync();
    }

    private static void SetStatus(PipelineTileViewModel tile, PipelineStatus status, string text)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            tile.Status = status;
            tile.StatusText = text;
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                tile.Status = status;
                tile.StatusText = text;
            });
        }
    }

    private Task ShowMessageAsync(string title, string message)
        => _dialogService?.ShowMessageAsync(title, message) ?? Task.CompletedTask;
}
