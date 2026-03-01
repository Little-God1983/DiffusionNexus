using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Progress information for a single install item (custom node clone or model download).
/// </summary>
public sealed record WorkloadInstallProgress
{
    /// <summary>Entity ID of the item (ModelDownload or GitRepository).</summary>
    public Guid ItemId { get; init; }

    /// <summary>Name of the item currently being processed.</summary>
    public required string ItemName { get; init; }

    /// <summary>Human-readable status message.</summary>
    public required string Message { get; init; }

    /// <summary>True when this particular item completed successfully.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>True when this particular item failed.</summary>
    public bool IsFailed { get; init; }
}

/// <summary>
/// Installs missing custom nodes (via git clone) and downloads missing models
/// for a workload configuration against a ComfyUI installation.
/// </summary>
public interface IWorkloadInstallService
{
    /// <summary>
    /// Clones missing custom nodes and downloads missing models.
    /// </summary>
    /// <param name="configuration">The SDK configuration that defines what to install.</param>
    /// <param name="comfyUIRootPath">Root path of the ComfyUI installation.</param>
    /// <param name="selectedNodes">Custom node check results the user selected for installation.</param>
    /// <param name="selectedModels">Model check results the user selected for installation.</param>
    /// <param name="selectedVramGb">VRAM selection in GB, or 0 to skip VRAM-based filtering.</param>
    /// <param name="progress">Reports per-item progress.</param>
    /// <param name="downloadProgress">Reports byte-level download progress for model files.</param>
    /// <param name="skipDownloadTokenProvider">Provides a cancellation token to skip the current download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary message.</returns>
    Task<string> InstallSelectedAsync(
        InstallationConfiguration configuration,
        string comfyUIRootPath,
        IReadOnlyList<CustomNodeCheckResult> selectedNodes,
        IReadOnlyList<ModelCheckResult> selectedModels,
        int selectedVramGb,
        IProgress<WorkloadInstallProgress>? progress = null,
        IProgress<DownloadProgress>? downloadProgress = null,
        Func<CancellationToken>? skipDownloadTokenProvider = null,
        CancellationToken cancellationToken = default);
}
