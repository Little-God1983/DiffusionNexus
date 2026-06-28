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
    /// Returns the backend currently selected for <paramref name="feature"/> by the default
    /// routing policy, or <c>null</c> if no backend is registered for it.
    /// </summary>
    IFeatureBackend? Resolve(Feature feature);

    /// <summary>
    /// Returns the backend to use for <paramref name="feature"/>. When
    /// <paramref name="preferredKind"/> is supplied (the user explicitly picked a backend),
    /// the backend registered for that kind is returned regardless of the default routing —
    /// or <c>null</c> if no backend is registered for that kind. When it is <c>null</c>, the
    /// default routing policy is used.
    /// </summary>
    IFeatureBackend? Resolve(Feature feature, BackendKind? preferredKind);

    /// <summary>
    /// Returns the backend kind the default routing policy maps <paramref name="feature"/> to,
    /// or <c>null</c> if the feature is unmapped. Used to seed a backend picker's initial value.
    /// </summary>
    BackendKind? GetDefaultKind(Feature feature);

    /// <summary>All registered backends, in a stable order, for building a backend picker.</summary>
    IReadOnlyList<IFeatureBackend> AvailableBackends { get; }
}
