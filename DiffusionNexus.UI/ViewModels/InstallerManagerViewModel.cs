using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;
using DiffusionNexus.Domain.Enums;


namespace DiffusionNexus.UI.ViewModels;

public partial class InstallerManagerViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly IInstallerPackageRepository _installerPackageRepository;

    [ObservableProperty]
    private string _welcomeMessage = "Welcome to the Installer Manager!";

    public InstallerManagerViewModel(
        IDialogService dialogService,
        IInstallerPackageRepository installerPackageRepository)
    {
        _dialogService = dialogService;
        _installerPackageRepository = installerPackageRepository;
    }

    [RelayCommand]
    private async Task AddExistingInstallationAsync()
    {
        // 1. Pick Folder
        var path = await _dialogService.ShowOpenFolderDialogAsync("Select Installation Folder");
        if (string.IsNullOrEmpty(path)) return;

        // 2. Show Dialog to details
        var result = await _dialogService.ShowAddExistingInstallationDialogAsync(path);
        
        if (result.IsCancelled) return;

        // 3. Save to Database
        var package = new InstallerPackage
        {
            Name = result.Name,
            InstallationPath = result.InstallationPath,
            Type = result.Type,
            ExecutablePath = result.ExecutablePath,
            Arguments = string.Empty, // Default empty
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _installerPackageRepository.AddAsync(package);
            await _dialogService.ShowMessageAsync("Success", $"Successfully added {package.Name}");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Error", $"Failed to save installation: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StartNewInstallationAsync()
    {
        await _dialogService.ShowMessageAsync("Coming Soon", "New installation feature is under development.");
    }
}
