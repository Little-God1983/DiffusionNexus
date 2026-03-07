using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Lightweight display model for an installer instance tab in the Unified Console.
/// Tracks running state, detected URL, and provides identity for Start/Stop commands.
/// </summary>
public partial class InstanceTabItem : ObservableObject
{
    /// <summary>
    /// The InstallerPackage database ID (used as instanceId for IInstanceProcessManager).
    /// </summary>
    public int PackageId { get; }

    /// <summary>
    /// Display name of the installation.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The installer type (ComfyUI, Forge, etc.).
    /// </summary>
    public InstallerType Type { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartButton))]
    [NotifyPropertyChangedFor(nameof(ShowStopButtons))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _detectedWebUrl;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Whether this installation is the default for its type.
    /// </summary>
    [ObservableProperty]
    private bool _isDefault;

    /// <summary>
    /// Whether a newer version is available on the remote.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    private bool _isUpdateAvailable;

    /// <summary>
    /// Whether an update operation is currently running.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    private bool _isUpdating;

    /// <summary>
    /// Human-readable summary of the update status (e.g. "3 commits behind origin/main").
    /// </summary>
    [ObservableProperty]
    private string? _updateSummary;

    /// <summary>
    /// The installation root path (needed for update operations).
    /// </summary>
    public string InstallationPath { get; init; } = string.Empty;

    /// <summary>
    /// Show the Start button when not running.
    /// </summary>
    public bool ShowStartButton => !IsRunning;

    /// <summary>
    /// Show Stop/Restart/WebUI when running.
    /// </summary>
    public bool ShowStopButtons => IsRunning;

    /// <summary>
    /// Show the Update button when an update is available, not currently updating, and not running.
    /// </summary>
    public bool ShowUpdateButton => IsUpdateAvailable && !IsUpdating && !IsRunning;

    /// <summary>
    /// Small logo for the tab.
    /// </summary>
    public Bitmap? Logo { get; }

    public InstanceTabItem(int packageId, string name, InstallerType type)
    {
        PackageId = packageId;
        Name = name;
        Type = type;
        Logo = LoadLogo(type);
    }

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
        catch
        {
            return null;
        }
    }
}
