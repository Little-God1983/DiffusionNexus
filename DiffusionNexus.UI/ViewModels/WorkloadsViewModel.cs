using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Workloads dialog showing ComfyUI workloads
/// from the SDK database, split into two tabs by <see cref="WorkloadTargetType"/>.
/// </summary>
public partial class WorkloadsViewModel : ViewModelBase
{
    private readonly IConfigurationRepository _configurationRepository;

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

    public WorkloadsViewModel(IConfigurationRepository configurationRepository)
    {
        ArgumentNullException.ThrowIfNull(configurationRepository);
        _configurationRepository = configurationRepository;
    }

    /// <summary>
    /// Loads ComfyUI workloads from the SDK database and splits them by target type.
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

            foreach (var config in configurations)
            {
                if (config.Repository.Type != RepositoryType.ComfyUI)
                    continue;

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
}
