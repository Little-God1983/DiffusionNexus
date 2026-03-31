using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Unified service for checking whether ComfyUI-backed features have all their
/// prerequisites satisfied (server online, custom nodes installed, models present).
/// 
/// <para>
/// Consumers call <see cref="CheckFeatureAsync"/> with a <see cref="ComfyUIFeature"/>
/// and receive a <see cref="FeatureReadinessResult"/> describing what is missing, if anything.
/// The service delegates to <see cref="IComfyUIWrapperService"/> for live server queries
/// and uses a feature registry to know which nodes/models each feature needs.
/// </para>
/// </summary>
public interface IComfyUIReadinessService
{
    /// <summary>
    /// Checks whether the ComfyUI server is reachable and all prerequisites for the given
    /// feature are installed.
    /// </summary>
    /// <param name="feature">The feature to check prerequisites for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result describing server status, missing nodes, missing models, and warnings.</returns>
    Task<FeatureReadinessResult> CheckFeatureAsync(ComfyUIFeature feature, CancellationToken ct = default);

    /// <summary>
    /// Lightweight check: is the ComfyUI server reachable?
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the server responded to a health check.</returns>
    Task<bool> IsServerOnlineAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the requirements declaration for a given feature without performing any network calls.
    /// Useful for displaying what a feature needs in the UI.
    /// </summary>
    /// <param name="feature">The feature to look up.</param>
    /// <returns>The requirements, or <c>null</c> if the feature is not registered.</returns>
    ComfyUIFeatureRequirements? GetRequirements(ComfyUIFeature feature);
}
