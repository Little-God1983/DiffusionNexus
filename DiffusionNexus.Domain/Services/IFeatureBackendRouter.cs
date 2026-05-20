using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Decides which <see cref="IFeatureBackend"/> answers readiness for a given feature.
/// The only place in the system that maps "feature X → backend Y" — re-pointing a feature
/// at a different backend means a router change, not view-model changes.
/// </summary>
public interface IFeatureBackendRouter
{
    /// <summary>
    /// Returns the backend currently selected for <paramref name="feature"/>, or
    /// <c>null</c> if no backend is registered for it.
    /// </summary>
    IFeatureBackend? Resolve(Feature feature);
}
