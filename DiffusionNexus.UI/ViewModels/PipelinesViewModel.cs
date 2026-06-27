using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Pipelines;
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

    /// <summary>The pipeline tiles displayed in the gallery.</summary>
    public ObservableCollection<PipelineTileViewModel> Pipelines { get; } = new();

    /// <summary>Design-time constructor (also used as a safe fallback).</summary>
    public PipelinesViewModel()
    {
        Pipelines.Add(new PipelineTileViewModel());
    }

    public PipelinesViewModel(
        IPipelineManifestProvider manifestProvider,
        IPipelineAssetInstaller installer,
        IDialogService? dialogService = null,
        IUnifiedLogger? unifiedLogger = null)
    {
        _manifestProvider = manifestProvider ?? throw new ArgumentNullException(nameof(manifestProvider));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _dialogService = dialogService;
        _unifiedLogger = unifiedLogger;

        foreach (var manifest in _manifestProvider.All())
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
    private async Task OpenPipelineAsync(PipelineTileViewModel? tile)
    {
        if (tile is null)
            return;

        Logger.Information("Pipeline tile opened: {PipelineId}", tile.Id);

        if (_installer is null || _manifestProvider is null)
            return; // design-time / not wired

        if (tile.IsBusy)
            return;

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
                await ShowMessageAsync(tile.Title,
                    "All required models and LoRAs for this pipeline are installed and ready.");
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
