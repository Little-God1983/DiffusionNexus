using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of checking whether an update is available for an installation.
/// </summary>
/// <param name="IsUpdateAvailable">True if the remote has commits not yet pulled.</param>
/// <param name="CurrentHash">Short hash of the current HEAD commit.</param>
/// <param name="RemoteHash">Short hash of the latest remote commit (null if check failed).</param>
/// <param name="Summary">Human-readable summary (e.g. "3 commits behind origin/main").</param>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string? CurrentHash,
    string? RemoteHash,
    string? Summary);

/// <summary>
/// Result of performing an update operation.
/// </summary>
/// <param name="Success">Whether the update completed without errors.</param>
/// <param name="NewHash">Short hash of HEAD after the update.</param>
/// <param name="Message">Summary of what happened.</param>
public sealed record UpdateResult(
    bool Success,
    string? NewHash,
    string Message);

/// <summary>
/// Service for checking and applying updates to installer packages.
/// Implementations are type-specific (e.g. ComfyUI has backend + frontend repos).
/// </summary>
public interface IInstallerUpdateService
{
    /// <summary>
    /// The installer types this service can handle.
    /// </summary>
    IReadOnlySet<InstallerType> SupportedTypes { get; }

    /// <summary>
    /// Checks whether updates are available by running git fetch and comparing local/remote HEADs.
    /// Does NOT modify the working tree.
    /// </summary>
    /// <param name="installationPath">Root path of the installation.</param>
    /// <param name="progress">Optional progress callback for UI feedback.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        string installationPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Applies the update (git pull) to the installation.
    /// For ComfyUI this updates backend first, then frontend.
    /// The instance MUST be stopped before calling this.
    /// </summary>
    /// <param name="installationPath">Root path of the installation.</param>
    /// <param name="progress">Optional progress callback for UI feedback.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<UpdateResult> UpdateAsync(
        string installationPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
