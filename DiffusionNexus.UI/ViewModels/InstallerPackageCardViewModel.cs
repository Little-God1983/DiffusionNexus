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
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    private bool _isUpdating;

    /// <summary>
    /// Live status message shown on the card during an update (e.g. "Fetching latest changes...").
    /// </summary>
    [ObservableProperty]
    private string? _updateStatusMessage;

    [ObservableProperty]
    private bool _isDefault;

    /// <summary>
    /// True when the installation folder no longer exists on disk.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLaunchButton))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    [NotifyPropertyChangedFor(nameof(ShowWorkloadsButton))]
    [NotifyPropertyChangedFor(nameof(ShowActionsPanel))]
    private bool _isMissing;

    // ── Process state ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLaunchButton))]
    [NotifyPropertyChangedFor(nameof(ShowRunningControls))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    [NotifyPropertyChangedFor(nameof(ShowActionsPanel))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _detectedWebUrl;

    /// <summary>
    /// True when this card represents the built-in Diffusion Nexus Engine (the
    /// app-owned ComfyUI). Not derived from <see cref="Type"/>: the backing row is an
    /// ordinary <see cref="InstallerType.ComfyUI"/> installation so the rest of the app
    /// (model roots, extra_model_paths discovery) treats it normally.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLaunchButton))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    [NotifyPropertyChangedFor(nameof(ShowWorkloadsButton))]
    [NotifyPropertyChangedFor(nameof(ShowActionsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowEngineInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowEngineWorkloadsButton))]
    private bool _isEngine;

    /// <summary>True when the engine has been installed (a backing row exists on disk).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEngineInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowEngineWorkloadsButton))]
    private bool _isEngineInstalled;

    /// <summary>True while the engine install is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEngineInstallButton))]
    private bool _isEngineInstalling;

    /// <summary>Live status line shown on the engine tile during install.</summary>
    [ObservableProperty]
    private string? _engineStatusMessage;

    /// <summary>Install progress 0-100; 0 when idle.</summary>
    [ObservableProperty]
    private double _engineProgressPercent;

    /// <summary>
    /// True when not running, not missing, AND this is not a non-launchable
    /// pseudo-installer (e.g. Diffusion Nexus Core or the Engine) — show the Launch button.
    /// </summary>
    public bool ShowLaunchButton => !IsRunning && !IsMissing && !IsCore && !IsEngine;

    /// <summary>
    /// True when running — show Stop/Restart/Console buttons.
    /// </summary>
    public bool ShowRunningControls => IsRunning;

    /// <summary>
    /// True when an update is available, not currently updating, not running,
    /// not missing, and not a Core or Engine pseudo-card.
    /// </summary>
    public bool ShowUpdateButton => IsUpdateAvailable && !IsUpdating && !IsRunning && !IsMissing && !IsCore && !IsEngine;

    /// <summary>
    /// True when this installation is a ComfyUI installation.
    /// </summary>
    public bool IsComfyUi => Type == InstallerType.ComfyUI;

    /// <summary>
    /// True when this card represents the built-in Diffusion Nexus Core pseudo-installer.
    /// Core cards have no Launch button and always show the Workloads button.
    /// </summary>
    public bool IsCore => Type == InstallerType.DiffusionNexusCore;

    /// <summary>
    /// True when the Workloads button should be visible. ComfyUI installations
    /// have curated model bundles, but only when the install still exists on
    /// disk; Core always offers its captioning/embedding workloads regardless
    /// of disk state. The Engine tile uses its own dedicated
    /// <see cref="ShowEngineWorkloadsButton"/> instead.
    /// </summary>
    public bool ShowWorkloadsButton => !IsEngine && (IsCore || (IsComfyUi && !IsMissing));

    /// <summary>Install button: engine tile, not yet installed, not currently installing.</summary>
    public bool ShowEngineInstallButton => IsEngine && !IsEngineInstalled && !IsEngineInstalling;

    /// <summary>Workloads button on the engine tile: only once the engine exists.</summary>
    public bool ShowEngineWorkloadsButton => IsEngine && IsEngineInstalled;

    /// <summary>
    /// True when the bottom action panel (Launch + Workloads container) should
    /// be visible at all. The container is hidden when neither action applies
    /// — e.g. when the install is currently running, missing on disk, and not
    /// Core. Always true for the Engine tile, whose own Install/Workloads
    /// buttons live inside this same panel. Used by the view to avoid leaving
    /// an empty gap on the card.
    /// </summary>
    public bool ShowActionsPanel => ShowLaunchButton || ShowWorkloadsButton || IsEngine;

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
    /// Raised when the user requests to view workloads for this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? WorkloadsRequested;

    /// <summary>
    /// Raised when the user requests to open settings for this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? SettingsRequested;

    /// <summary>
    /// Raised when the user requests to make this installation the default for its type.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? MakeDefaultRequested;

    /// <summary>
    /// Raised when the user requests to update this installation.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? UpdateRequested;

    /// <summary>
    /// Raised when the user presses Install on the engine tile.
    /// </summary>
    public event Func<InstallerPackageCardViewModel, Task>? EngineInstallRequested;

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
        _isDefault = package.IsDefault;

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
    /// Creates the singleton "Diffusion Nexus Core" card shown at the top of
    /// the Installer Manager. This card has no backing database row — it
    /// represents the in-process Core capabilities and only exposes the
    /// Workloads action.
    /// </summary>
    public static InstallerPackageCardViewModel CreateCoreCard()
    {
        return new InstallerPackageCardViewModel(forCore: true);
    }

    private InstallerPackageCardViewModel(bool forCore)
    {
        if (!forCore)
            throw new ArgumentException("Use the public constructor for database-backed packages.", nameof(forCore));

        Id = 0;
        _name = "Diffusion Nexus Core";
        _type = InstallerType.DiffusionNexusCore;
        _installationPath = string.Empty;
        _executablePath = null;
        _arguments = string.Empty;
        _isUpdateAvailable = false;
        _isDefault = false;
        _versionDisplay = "Built-in";

        LogoImage = LoadLogo(InstallerType.DiffusionNexusCore);
    }

    /// <summary>
    /// Creates the singleton "Diffusion Nexus Engine" tile. Pass the app-managed
    /// <see cref="InstallerPackage"/> when the engine is installed, or <c>null</c> when it
    /// is not — the tile then offers Install instead of Workloads.
    /// </summary>
    public static InstallerPackageCardViewModel CreateEngineCard(InstallerPackage? installed)
    {
        var card = installed is null
            ? new InstallerPackageCardViewModel(forCore: true)
            : new InstallerPackageCardViewModel(installed);

        card.Name = "Diffusion Nexus Engine";
        card.Type = InstallerType.ComfyUI;
        card.IsEngine = true;
        card.IsEngineInstalled = installed is not null;
        card.VersionDisplay = installed is null ? "Not installed" : "App-managed";
        card.InstallationPath = installed?.InstallationPath ?? string.Empty;

        return card;
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
            InstallerType.AIToolkit => "avares://DiffusionNexus.UI/Assets/InstallerManager/AI-Toolkit-Logo.png",
            _ => "avares://DiffusionNexus.UI/Assets/Installer.png"
        };

        return SafeAssetBitmap.Load(assetPath);
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
    private async Task ShowWorkloadsAsync()
    {
        if (WorkloadsRequested is not null)
            await WorkloadsRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (SettingsRequested is not null)
            await SettingsRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task MakeDefaultAsync()
    {
        if (MakeDefaultRequested is not null)
            await MakeDefaultRequested.Invoke(this);
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

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (UpdateRequested is not null)
            await UpdateRequested.Invoke(this);
    }

    [RelayCommand]
    private async Task InstallEngineAsync()
    {
        if (EngineInstallRequested is not null)
            await EngineInstallRequested.Invoke(this);
    }
}
