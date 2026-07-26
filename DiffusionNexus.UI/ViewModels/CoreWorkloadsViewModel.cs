using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Pipelines;
using DiffusionNexus.UI.Views.Dialogs;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Diffusion Nexus Core workloads dialog — the management surface for the
/// static/built-in Core instance (which does not use ComfyUI). Hosts two tabs:
/// <list type="bullet">
///   <item><description><b>Pipelines</b> — guided image pipelines (e.g. Anime-To-Real) with a
///   Full/Partial/None status and a Details button, downloaded via the pipeline asset installer.</description></item>
///   <item><description><b>Captioning Models</b> — the existing captioning-models list.</description></item>
/// </list>
/// </summary>
public partial class CoreWorkloadsViewModel : ViewModelBase
{
    /// <summary>VRAM tiers offered for pipeline diffusion-model variants (GGUF quantizations).</summary>
    private static readonly int[] PipelineVramTiers = [8, 12, 16, 24, 32];

    private readonly IPipelineManifestProvider? _manifestProvider;
    private readonly IPipelineAssetInstaller? _installer;
    private readonly Services.Diffusion.IModelFolderCatalog? _modelFolderCatalog;

    /// <summary>Pipeline workload rows shown on the "Pipelines" tab.</summary>
    public ObservableCollection<WorkloadItemViewModel> Pipelines { get; } = [];

    /// <summary>
    /// Selectable download targets (Base Model Folders; the LocalAppData fallback when
    /// none are configured). Shown in the window's "Download to:" dropdown.
    /// </summary>
    public ObservableCollection<Services.Diffusion.ModelFolderOption> DownloadTargets { get; } = [];

    /// <summary>
    /// The download target chosen for installs started from this window. Per-window only —
    /// changing it does not touch the ⭐ default in Settings.
    /// </summary>
    [ObservableProperty]
    private Services.Diffusion.ModelFolderOption? _selectedDownloadTarget;

    /// <summary>Backing ViewModel for the "Captioning Models" tab.</summary>
    public CaptioningModelsDialogViewModel Captioning { get; }

    [ObservableProperty]
    private bool _isLoading;

    public CoreWorkloadsViewModel(
        CaptioningModelsDialogViewModel captioning,
        IPipelineManifestProvider? manifestProvider,
        IPipelineAssetInstaller? installer,
        Services.Diffusion.IModelFolderCatalog? modelFolderCatalog = null)
    {
        Captioning = captioning ?? throw new ArgumentNullException(nameof(captioning));
        _manifestProvider = manifestProvider;
        _installer = installer;
        _modelFolderCatalog = modelFolderCatalog;
    }

    /// <summary>
    /// Fills the "Download to:" dropdown from the model folder catalog and preselects
    /// the default target.
    /// </summary>
    private async Task LoadDownloadTargetsAsync()
    {
        if (_modelFolderCatalog is null)
        {
            return;
        }

        try
        {
            var targets = await _modelFolderCatalog.GetDownloadTargetsAsync();

            DownloadTargets.Clear();
            foreach (var target in targets)
            {
                DownloadTargets.Add(target);
            }

            SelectedDownloadTarget = DownloadTargets.FirstOrDefault(t => t.IsDefault)
                ?? DownloadTargets.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to load base model folder download targets.");
        }
    }

    /// <summary>
    /// Resolves the download root for pipeline installs: the window's dropdown selection
    /// when available, else the catalog default, else the LocalAppData fallback.
    /// </summary>
    private async Task<string> ResolveDownloadRootAsync(CancellationToken ct)
    {
        if (SelectedDownloadTarget is { } selected)
        {
            return selected.Path;
        }

        if (_modelFolderCatalog is not null)
        {
            return await _modelFolderCatalog.GetDefaultDownloadRootAsync(ct);
        }

        return Services.Diffusion.ModelFolderCatalog.FallbackRoot;
    }

    /// <summary>Builds the pipeline rows and checks each one's on-disk readiness.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        await LoadDownloadTargetsAsync();

        if (_manifestProvider is null || _installer is null)
        {
            return;
        }

        try
        {
            IsLoading = true;
            Pipelines.Clear();

            foreach (var manifest in _manifestProvider.All())
            {
                Pipelines.Add(new WorkloadItemViewModel(
                    Guid.NewGuid(), manifest.Title, manifest.Description, 1, 0)
                {
                    ConfiguredVramProfiles = PipelineVramTiers,
                    PipelineManifest = manifest,
                });
            }

            foreach (var item in Pipelines)
            {
                if (item.PipelineManifest is { } manifest)
                {
                    await CheckPipelineItemAsync(item, manifest);
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ShowDetailsAsync(WorkloadItemViewModel? item)
    {
        if (item?.PipelineManifest is not { } manifest)
        {
            return;
        }

        await ShowPipelineDetailsAsync(item, manifest);
    }

    /// <summary>Computes a pipeline workload's status badge from its on-disk readiness.</summary>
    private async Task CheckPipelineItemAsync(WorkloadItemViewModel item, PipelineManifest manifest)
    {
        if (_installer is null)
        {
            item.Status = "None";
            return;
        }

        try
        {
            var readiness = await _installer.CheckAsync(manifest);
            item.PipelineReadiness = readiness;
            item.Status = MapPipelineStatus(readiness);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to check pipeline workload '{Name}'", item.Name);
            item.Status = "Error";
        }
    }

    private static string MapPipelineStatus(PipelineReadiness readiness)
    {
        var total = readiness.Assets.Count;
        if (total == 0)
        {
            return "None";
        }

        var present = readiness.Assets.Count(a => a.IsPresent);
        return present == 0 ? "None" : present == total ? "Full" : "Partial";
    }

    /// <summary>
    /// Shows the shared <see cref="WorkloadDetailsDialog"/> for a pipeline workload, populated from
    /// its <see cref="PipelineReadiness"/> and wired to install via the pipeline asset installer.
    /// </summary>
    private async Task ShowPipelineDetailsAsync(WorkloadItemViewModel item, PipelineManifest manifest)
    {
        if (_installer is null)
        {
            return;
        }

        PipelineReadiness readiness;
        try
        {
            readiness = item.PipelineReadiness ?? await _installer.CheckAsync(manifest);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load pipeline details for {Name}", item.Name);
            return;
        }

        var detailItems = new ObservableCollection<WorkloadDetailItemViewModel>();
        foreach (var asset in readiness.Assets)
        {
            var details = asset.IsPresent
                ? asset.ResolvedFileName ?? "Installed"
                : "Missing — will be downloaded";

            detailItems.Add(new WorkloadDetailItemViewModel(
                Guid.NewGuid(), asset.Name, "Model", asset.IsPresent, details,
                isPlaceholder: false,
                hasVramProfiles: asset.Kind == PipelineAssetKind.DiffusionModel));
        }

        var present = readiness.Assets.Count(a => a.IsPresent);

        var dialog = new WorkloadDetailsDialog
        {
            Title = $"Details – {manifest.Title}",
            DetailItems = detailItems,
            Summary = $"{present}/{readiness.Assets.Count} required assets installed",
            ConfiguredVramProfiles = item.ConfiguredVramProfiles,
            InstallCallback = CreatePipelineInstallCallback(manifest, detailItems),
            RepairCallback = null
        };

        var parentWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (parentWindow is not null)
        {
            await dialog.ShowDialog(parentWindow);
        }

        if (dialog.DidInstall)
        {
            await CheckPipelineItemAsync(item, manifest);
        }
    }

    /// <summary>
    /// Builds the <see cref="WorkloadDetailsDialog.InstallCallback"/> for a pipeline workload.
    /// Downloads all currently-missing assets via the pipeline installer (byte-level progress is
    /// surfaced through the status-bar download coordinator); reports per-asset results so the
    /// dialog's rows turn green.
    /// </summary>
    private Func<IReadOnlyList<WorkloadDetailItemViewModel>, int, IProgress<WorkloadInstallProgress>,
        IProgress<DownloadProgress>, Func<CancellationToken>, CancellationToken, Task<string>>
        CreatePipelineInstallCallback(
            PipelineManifest manifest, ObservableCollection<WorkloadDetailItemViewModel> detailItems)
    {
        return async (selectedItems, vramGb, progress, downloadProgress, skipTokenProvider, ct) =>
        {
            if (_installer is null)
            {
                return "Pipeline installer unavailable.";
            }

            progress.Report(new WorkloadInstallProgress
            {
                ItemName = manifest.Title,
                Message = $"Installing assets for {manifest.Title}… (download progress is shown in the status bar)"
            });

            var downloadRoot = await ResolveDownloadRootAsync(ct);
            var result = await _installer.InstallMissingAsync(manifest, vramGb, downloadRoot, ct);

            foreach (var asset in result.Assets)
            {
                var id = detailItems.FirstOrDefault(d => d.ItemName == asset.Name)?.Id ?? Guid.Empty;
                progress.Report(new WorkloadInstallProgress
                {
                    ItemId = id,
                    ItemName = asset.Name,
                    Message = asset.IsPresent ? $"{asset.Name}: installed" : $"{asset.Name}: still missing",
                    IsSuccess = asset.IsPresent,
                    IsFailed = !asset.IsPresent
                });
            }

            return result.IsComplete
                ? "All pipeline assets installed."
                : $"{result.Missing.Count} asset(s) still missing.";
        };
    }
}
