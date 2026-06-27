using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Pipelines;
using DiffusionNexus.UI.Views.Dialogs;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Pipelines module — a tile gallery of guided image pipelines. Each tile
/// declares the model/LoRA assets its pipeline needs; clicking it downloads whatever is missing
/// (mirroring the Installer Manager's workload flow) into the default ComfyUI models tree, after
/// the user picks a VRAM tier for the diffusion model.
/// </summary>
public partial class PipelinesViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<PipelinesViewModel>();

    // VRAM tiers offered for the diffusion-model variant pick (GGUF quantizations).
    private static readonly int[] VramTiers = [8, 12, 16, 24, 32];

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
            // 1) Resolve the download target (default ComfyUI install's models tree).
            var root = await _installer.ResolveModelsRootAsync();
            if (string.IsNullOrEmpty(root))
            {
                await ShowMessageAsync("No ComfyUI installation",
                    "Pipeline models are stored in a ComfyUI installation's models folder, which the local renderer also reads.\n\n" +
                    "Add a ComfyUI installation in the Installer Manager first, then try again.");
                SetStatus(tile, PipelineStatus.NotInstalled, "No ComfyUI install");
                return;
            }

            // 2) What's already on disk? (searches every core model root, incl. extra_model_paths)
            var readiness = await _installer.CheckAsync(tile.Manifest);
            if (readiness.IsComplete)
            {
                SetStatus(tile, PipelineStatus.Ready, "Ready");
                await ShowMessageAsync(tile.Title,
                    "All required models and LoRAs for this pipeline are already installed.");
                return;
            }

            // 3) Pick a VRAM tier for the diffusion-model variant.
            var vramGb = await PickVramTierAsync();
            if (vramGb is null)
                return; // cancelled

            // 4) Confirm.
            var missingNames = string.Join("\n", readiness.Missing.Select(m => $"  • {m.Name}"));
            var proceed = await ShowConfirmAsync(tile.Title,
                $"Download the following missing assets for the {vramGb} GB profile?\n\n{missingNames}\n\n" +
                "Large model files may take a while. Progress is shown in the status bar.");
            if (!proceed)
                return;

            // 5) Install.
            tile.IsBusy = true;
            SetStatus(tile, PipelineStatus.Downloading, "Downloading…");
            _unifiedLogger?.Info(LogCategory.Download, "Pipelines",
                $"Installing assets for pipeline '{tile.Id}' (VRAM tier {vramGb} GB).");

            var result = await _installer.InstallMissingAsync(tile.Manifest, vramGb.Value);

            // 6) Report.
            if (result.IsComplete)
            {
                SetStatus(tile, PipelineStatus.Ready, "Ready");
                await ShowMessageAsync(tile.Title, "All pipeline assets are installed and ready.");
            }
            else
            {
                var stillMissing = string.Join("\n", result.Missing.Select(m => $"  • {m.Name}"));
                SetStatus(tile, PipelineStatus.NotInstalled, $"{result.Missing.Count} missing");
                await ShowMessageAsync(tile.Title,
                    $"Some assets are still missing:\n\n{stillMissing}");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(tile, PipelineStatus.NotInstalled, "Cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Pipeline install failed for {Id}.", tile.Id);
            SetStatus(tile, PipelineStatus.Error, "Failed");
            await ShowMessageAsync("Install failed", ex.Message);
        }
        finally
        {
            tile.IsBusy = false;
        }
    }

    private async Task<int?> PickVramTierAsync()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return null;

        var dialog = new VramSelectionDialog(VramTiers);
        await dialog.ShowDialog(owner);
        return dialog.SelectedVramGb;
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

    private Task<bool> ShowConfirmAsync(string title, string message)
        => _dialogService?.ShowConfirmAsync(title, message) ?? Task.FromResult(true);
}
