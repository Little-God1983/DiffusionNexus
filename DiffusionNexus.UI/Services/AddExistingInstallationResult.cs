using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services;

public record AddExistingInstallationResult(
    string Name,
    string InstallationPath,
    InstallerType Type,
    string ExecutablePath,
    string OutputFolderPath,
    bool IsCancelled = false)
{
    public static AddExistingInstallationResult Cancelled() => new(string.Empty, string.Empty, InstallerType.Unknown, string.Empty, string.Empty, true);
}
