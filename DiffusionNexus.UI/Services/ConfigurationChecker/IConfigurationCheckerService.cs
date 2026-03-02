using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;

namespace DiffusionNexus.UI.Services.ConfigurationChecker;

/// <summary>
/// Checks whether a ComfyUI instance has the custom nodes and models
/// expected by a given <see cref="InstallationConfiguration"/>.
/// Designed to be registered as a singleton and used across the application.
/// </summary>
public interface IConfigurationCheckerService
{
    /// <summary>
    /// Checks custom nodes and models for a configuration against the ComfyUI instance at
    /// <paramref name="comfyUIRootPath"/>.
    /// </summary>
    /// <param name="configuration">The SDK configuration to verify.</param>
    /// <param name="comfyUIRootPath">
    /// Root path of the ComfyUI installation. For portable installs this is
    /// the folder containing the <c>ComfyUI</c> subfolder (e.g. <c>C:\ComfyUI_windows_portable</c>).
    /// For manual installs this is the root that contains <c>models/</c> directly.
    /// </param>
    /// <param name="options">Optional check options (VRAM profile, model base folder, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ConfigurationCheckResult"/> with per-item details.</returns>
    Task<ConfigurationCheckResult> CheckConfigurationAsync(
        InstallationConfiguration configuration,
        string comfyUIRootPath,
        ConfigurationCheckOptions? options = null,
        CancellationToken cancellationToken = default);
}
