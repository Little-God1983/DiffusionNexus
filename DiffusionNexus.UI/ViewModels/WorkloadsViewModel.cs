using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
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
    private readonly string _comfyUIRootPath;

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
        string comfyUIRootPath)
    {
        ArgumentNullException.ThrowIfNull(configurationRepository);
        ArgumentNullException.ThrowIfNull(checkerService);
        ArgumentException.ThrowIfNullOrWhiteSpace(comfyUIRootPath);

        _configurationRepository = configurationRepository;
        _checkerService = checkerService;
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

            foreach (var config in comfyConfigurations)
            {
                var item = new WorkloadItemViewModel(
                    config.Id,
                    config.Name,
                    config.Description,
                    config.ConfigurationVersion,
                    config.ConfigurationSubVersion);

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
                node.Name, "Custom Node", node.IsInstalled, detail));
        }

        foreach (var model in item.CheckResult.ModelResults)
        {
            var detail = model.IsPlaceholder
                ? "Will be downloaded automatically when the workflow runs"
                : model.IsInstalled
                    ? model.FoundAtPath
                    : $"Searched {model.SearchedPaths.Count} location(s)";

            detailItems.Add(new WorkloadDetailItemViewModel(
                model.Name, "Model", model.IsInstalled, detail, model.IsPlaceholder));
        }

        var dialog = new WorkloadDetailsDialog
        {
            Title = $"Details – {item.Name}",
            DetailItems = detailItems,
            Summary = item.CheckResult.Summary
        };

        var parentWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (parentWindow is not null)
        {
            await dialog.ShowDialog(parentWindow);
        }
    }
}
