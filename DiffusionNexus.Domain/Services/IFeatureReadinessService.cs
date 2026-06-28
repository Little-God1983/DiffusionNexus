using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Backend-agnostic readiness service. View-models depend on this single contract; the
/// concrete backend that answers each check is decided by
/// <see cref="IFeatureBackendRouter"/>.
/// </summary>
public interface IFeatureReadinessService
{
    /// <summary>
    /// Checks whether <paramref name="feature"/> is ready to execute on its default backend.
    /// </summary>
    Task<FeatureReadinessResult> CheckAsync(Feature feature, CancellationToken ct = default);

    /// <summary>
    /// Checks whether <paramref name="feature"/> is ready to execute. When
    /// <paramref name="backendOverride"/> is supplied, readiness is evaluated against that
    /// backend (the one the user picked) instead of the default routing.
    /// </summary>
    Task<FeatureReadinessResult> CheckAsync(
        Feature feature,
        BackendKind? backendOverride,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the static metadata for <paramref name="feature"/> (display name etc.)
    /// without performing any network calls.
    /// </summary>
    FeatureRequirements? GetRequirements(Feature feature);

    /// <summary>The backends a user may pick from, for the readiness panel's backend picker.</summary>
    IReadOnlyList<BackendInfo> GetAvailableBackends();

    /// <summary>The backend the default routing maps <paramref name="feature"/> to (the picker's initial value).</summary>
    BackendKind? GetDefaultBackend(Feature feature);
}
