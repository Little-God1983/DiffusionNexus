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
    /// Checks whether <paramref name="feature"/> is ready to execute on its currently
    /// selected backend.
    /// </summary>
    Task<FeatureReadinessResult> CheckAsync(Feature feature, CancellationToken ct = default);

    /// <summary>
    /// Returns the static metadata for <paramref name="feature"/> (display name etc.)
    /// without performing any network calls.
    /// </summary>
    FeatureRequirements? GetRequirements(Feature feature);
}
