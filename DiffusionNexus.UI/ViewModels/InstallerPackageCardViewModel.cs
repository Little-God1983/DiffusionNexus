using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services;

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

    // ── Process state ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLaunchButton))]
    [NotifyPropertyChangedFor(nameof(ShowRunningControls))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _detectedWebUrl;

    /// <summary>
    /// True when not running — show the Launch button.
    /// </summary>
    public bool ShowLaunchButton => !IsRunning;

    /// <summary>
    /// True when running — show Stop/Restart/Console buttons.
    /// </summary>
    public bool ShowRunningControls => IsRunning;

    /// <summary>
    /// Console output lines captured from the process.
    /// </summary>
    public ObservableCollection<ConsoleOutputLine> ConsoleLines { get; } = [];

    // ── Events ──

    /// <summary>
    /// Raised when the user requests to launch this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? LaunchRequested;

    /// <summary>
    /// Raised when the user requests to stop this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? StopRequested;

    /// <summary>
    /// Raised when the user requests to restart this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? RestartRequested;

    /// <summary>
    /// Raised when the user requests to open the console for this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? ConsoleRequested;

    /// <summary>
    /// Raised when the user requests to remove this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? RemoveRequested;

    /// <summary>
    /// Raised when the user requests to delete this installation from disk.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? DeleteFromDiskRequested;

    /// <summary>
    /// Raised when the user requests to open the installation folder.
    /// </summary>
    public event Action<InstallerPackageCardViewModel>? OpenFolderRequested;

    /// <summary>
    /// Raised when the user requests to open settings for this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? SettingsRequested;

    /// <summary>
    /// Logo image resolved from the installer type.
    /// </summary>
    public Bitmap? LogoImage { get; }

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

        LogoImage = LoadLogo(package.Type);
    }

    /// <summary>
    /// Loads the logo bitmap for the given installer type from embedded assets.
    /// Falls back to the default Installer.png when no specific logo exists.
    /// </summary>
    private static Bitmap? LoadLogo(InstallerType type)
    {
        var assetPath = type switch
        {
            InstallerType.Automatic1111 => "avares://DiffusionNexus.UI/Assets/InstallerManager/Automatic-Logo.png",
            InstallerType.Forge => "avares://DiffusionNexus.UI/Assets/InstallerManager/ForgeUI-Logo.png",
            InstallerType.ComfyUI => "avares://DiffusionNexus.UI/Assets/InstallerManager/ComfyUI-logo.png",
            _ => "avares://DiffusionNexus.UI/Assets/Installer.png"
        };

        try
        {
            using var assetStream = AssetLoader.Open(new Uri(assetPath));
            var ms = new MemoryStream();
            assetStream.CopyTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to load installer logo from {AssetPath}", assetPath);
            return null;
        }
    }

    /// <summary>
    /// Appends a console line on the UI thread.
    /// </summary>
    public void AppendConsoleLine(ConsoleOutputLine line)
    {
        Dispatcher.UIThread.Post(() => ConsoleLines.Add(line));
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (LaunchRequested is not null)
            await LaunchRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (StopRequested is not null)
            await StopRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (RestartRequested is not null)
            await RestartRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task ShowConsoleAsync()
    {
        if (ConsoleRequested is not null)
            await ConsoleRequested.Invoke(this);
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        if (string.IsNullOrWhiteSpace(DetectedWebUrl)) return;

        try
        {
            // TODO: Linux Implementation - use xdg-open on Linux
            Process.Start(new ProcessStartInfo(DetectedWebUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open Web UI at {Url}", DetectedWebUrl);
        }
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

    [RelayCommand]
    private async Task DeleteFromDiskAsync()
    {
        if (DeleteFromDiskRequested is not null)
            await DeleteFromDiskRequested.Invoke(this);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        OpenFolderRequested?.Invoke(this);
    }
}
