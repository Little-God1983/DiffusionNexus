using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Views.Dialogs;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Workloads dialog showing ComfyUI workloads
/// from the SDK database, split into two tabs by <see cref="WorkloadTargetType"/>.
/// </summary>
public partial class WorkloadsViewModel : ViewModelBase
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IConfigurationCheckerService _checkerService;
    private readonly IWorkloadInstallService _installService;
    private readonly string _comfyUIRootPath;

    /// <summary>
    /// Cached configurations so we can pass them to the install service.
    /// </summary>
    private List<InstallationConfiguration> _loadedConfigurations = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Workloads targeting DiffusionNexusCore (shown on "Diffusion Nexus" tab).
    /// </summary>
    public ObservableCollection<WorkloadItemViewModel> DiffusionNexusWorkloads { get; } = [];

    /// <summary>
    /// Workloads targeting Installer (shown on "Installer" tab).
    /// </summary>
    public ObservableCollection<WorkloadItemViewModel> InstallerWorkloads { get; } = [];

    public WorkloadsViewModel(
        IConfigurationRepository configurationRepository,
        IConfigurationCheckerService checkerService,
        IWorkloadInstallService installService,
        string comfyUIRootPath)
    {
        ArgumentNullException.ThrowIfNull(configurationRepository);
        ArgumentNullException.ThrowIfNull(checkerService);
        ArgumentNullException.ThrowIfNull(installService);
        ArgumentException.ThrowIfNullOrWhiteSpace(comfyUIRootPath);

        _configurationRepository = configurationRepository;
        _checkerService = checkerService;
        _installService = installService;
        _comfyUIRootPath = comfyUIRootPath;
    }

    /// <summary>
    /// Loads ComfyUI workloads from the SDK database, splits them by target type,
    /// and runs the configuration checker against the local ComfyUI installation.
    /// </summary>
    [RelayCommand]
    private async Task LoadWorkloadsAsync()
    {
        try
        {
            IsLoading = true;
            DiffusionNexusWorkloads.Clear();
            InstallerWorkloads.Clear();

            var configurations = await _configurationRepository.GetAllAsync();

            Serilog.Log.Information("WorkloadsViewModel: Loaded {Count} configurations from SDK database", configurations.Count);

            var comfyConfigurations = configurations
                .Where(c => c.Repository.Type == RepositoryType.ComfyUI)
                .ToList();

            _loadedConfigurations = comfyConfigurations;

            foreach (var config in comfyConfigurations)
            {
                var item = new WorkloadItemViewModel(
                    config.Id,
                    config.Name,
                    config.Description,
                    config.ConfigurationVersion,
                    config.ConfigurationSubVersion)
                {
                    ConfiguredVramProfiles = ParseVramProfiles(config.Vram.VramProfiles)
                };

                if (config.WorkloadTarget == WorkloadTargetType.DiffusionNexusCore)
                {
                    DiffusionNexusWorkloads.Add(item);
                }
                else
                {
                    InstallerWorkloads.Add(item);
                }
            }

            Serilog.Log.Information(
                "WorkloadsViewModel: {DnCount} DiffusionNexus workloads, {InsCount} Installer workloads",
                DiffusionNexusWorkloads.Count, InstallerWorkloads.Count);

            // Run checks in parallel after populating the lists
            await CheckAllWorkloadsAsync(comfyConfigurations);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load workloads from SDK database");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Runs the configuration checker for every workload item.
    /// </summary>
    private async Task CheckAllWorkloadsAsync(IReadOnlyList<InstallationConfiguration> configurations)
    {
        var allItems = DiffusionNexusWorkloads.Concat(InstallerWorkloads).ToList();

        foreach (var item in allItems)
        {
            var config = configurations.FirstOrDefault(c => c.Id == item.Id);
            if (config is null)
            {
                continue;
            }

            try
            {
                var result = await _checkerService.CheckConfigurationAsync(
                    config, _comfyUIRootPath);

                item.CheckResult = result;
                item.Status = result.OverallStatus.ToString();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to check configuration '{Name}'", item.Name);
                item.Status = "Error";
            }
        }
    }

    /// <summary>
    /// Shows the detail dialog for a specific workload item.
    /// </summary>
    [RelayCommand]
    private async Task ShowDetailsAsync(WorkloadItemViewModel? item)
    {
        if (item?.CheckResult is null)
        {
            return;
        }

        var detailItems = new ObservableCollection<WorkloadDetailItemViewModel>();

        foreach (var node in item.CheckResult.CustomNodeResults)
        {
            var detail = node.IsInstalled
                ? node.ExpectedPath
                : $"Expected at: {node.ExpectedPath}";

            detailItems.Add(new WorkloadDetailItemViewModel(
                node.Id, node.Name, "Custom Node", node.IsInstalled, detail));
        }

        foreach (var model in item.CheckResult.ModelResults)
        {
            var detail = model.IsPlaceholder
                ? "Will be downloaded automatically when the workflow runs"
                : model.IsInstalled
                    ? model.FoundAtPath
                    : $"Searched {model.SearchedPaths.Count} location(s)";

            detailItems.Add(new WorkloadDetailItemViewModel(
                model.Id, model.Name, "Model", model.IsInstalled, detail,
                model.IsPlaceholder, model.HasVramProfiles));
        }

        var dialog = new WorkloadDetailsDialog
        {
            Title = $"Details – {item.Name}",
            DetailItems = detailItems,
            Summary = item.CheckResult.Summary,
            ConfiguredVramProfiles = item.ConfiguredVramProfiles,
            InstallCallback = CreateInstallCallback(item)
        };

        var parentWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (parentWindow is not null)
        {
            await dialog.ShowDialog(parentWindow);
        }

        // Re-check the workload status if installation ran
        if (dialog.DidInstall)
        {
            await ReCheckWorkloadAsync(item);
        }
    }

    /// <summary>
    /// Creates the install callback that the dialog invokes with live progress.
    /// </summary>
    private Func<IReadOnlyList<WorkloadDetailItemViewModel>, int, IProgress<WorkloadInstallProgress>, CancellationToken, Task<string>>
        CreateInstallCallback(WorkloadItemViewModel item)
    {
        return async (selectedItems, vramGb, progress, ct) =>
        {
            var config = _loadedConfigurations.FirstOrDefault(c => c.Id == item.Id);
            if (config is null)
            {
                throw new InvalidOperationException($"Configuration {item.Id} not found");
            }

            // Separate nodes vs models by matching IDs back to the check results
            var nodeIds = new HashSet<Guid>(
                selectedItems.Where(i => i.Category == "Custom Node").Select(i => i.Id));
            var modelIds = new HashSet<Guid>(
                selectedItems.Where(i => i.Category == "Model").Select(i => i.Id));

            var selectedNodes = item.CheckResult!.CustomNodeResults
                .Where(n => nodeIds.Contains(n.Id))
                .ToList();
            var selectedModels = item.CheckResult!.ModelResults
                .Where(m => modelIds.Contains(m.Id))
                .ToList();

            return await _installService.InstallSelectedAsync(
                config, _comfyUIRootPath,
                selectedNodes, selectedModels,
                vramGb, progress, ct);
        };
    }

    /// <summary>
    /// Re-checks the workload after installation so the status column updates.
    /// </summary>
    private async Task ReCheckWorkloadAsync(WorkloadItemViewModel item)
    {
        var config = _loadedConfigurations.FirstOrDefault(c => c.Id == item.Id);
        if (config is null) return;

        try
        {
            var newResult = await _checkerService.CheckConfigurationAsync(
                config, _comfyUIRootPath);

            item.CheckResult = newResult;
            item.Status = newResult.OverallStatus.ToString();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to re-check workload {Name}", item.Name);
        }
    }

    /// <summary>
    /// Parses the comma-separated VRAM profiles string (e.g. "8,16,24,24+")
    /// into an array of integer GB values, matching the installer behaviour.
    /// </summary>
    private static int[] ParseVramProfiles(string? vramProfiles)
    {
        if (string.IsNullOrWhiteSpace(vramProfiles))
        {
            return [];
        }

        return vramProfiles
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p.Replace("GB", "").Replace("+", ""), out var val) ? val : 0)
            .Where(v => v > 0)
            .ToArray();
    }
}
