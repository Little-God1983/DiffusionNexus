using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for a single installer package card displayed in the Installer Manager.
/// </summary>
public partial class InstallerPackageCardViewModel : ViewModelBase
{
    /// <summary>
    /// The underlying database entity ID.
    /// </summary>
    public int Id { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _versionDisplay = string.Empty;

    [ObservableProperty]
    private InstallerType _type;

    [ObservableProperty]
    private string _installationPath = string.Empty;

    [ObservableProperty]
    private string? _executablePath;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    /// <summary>
    /// Raised when the user requests to launch this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? LaunchRequested;

    /// <summary>
    /// Raised when the user requests to remove this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? RemoveRequested;

    /// <summary>
    /// Raised when the user requests to open settings for this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? SettingsRequested;

    public InstallerPackageCardViewModel(InstallerPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        Id = package.Id;
        _name = package.Name;
        _type = package.Type;
        _installationPath = package.InstallationPath;
        _executablePath = package.ExecutablePath;
        _arguments = package.Arguments;
        _isUpdateAvailable = package.IsUpdateAvailable;

        // Build "branch@hash" display string
        var branch = string.IsNullOrWhiteSpace(package.Branch) ? null : package.Branch;
        var version = string.IsNullOrWhiteSpace(package.Version) ? null : package.Version;
        _versionDisplay = (branch, version) switch
        {
            (not null, not null) => $"{branch}@{version}",
            (not null, null) => branch,
            (null, not null) => version,
            _ => string.Empty
        };
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (LaunchRequested is not null)
            await LaunchRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (SettingsRequested is not null)
            await SettingsRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (RemoveRequested is not null)
            await RemoveRequested.Invoke(this);
    }
}
