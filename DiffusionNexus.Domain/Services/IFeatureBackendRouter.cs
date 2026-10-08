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
    /// <c>null</c> if no backend is registered for it. For the features in
    /// <c>FeatureBackendRouter.ServerModeFeatures</c> the answer follows Settings → ComfyUI Server
    /// and may change between calls.
    /// </summary>
    IFeatureBackend? Resolve(Feature feature);

    /// <summary>
    /// Asynchronous <see cref="Resolve"/>: reads the Settings → ComfyUI Server mode without blocking the
    /// caller. The default answers with <see cref="Resolve"/>, for routers that need no I/O.
    /// </summary>
    Task<IFeatureBackend?> ResolveAsync(Feature feature, CancellationToken ct = default) =>
        Task.FromResult(Resolve(feature));
}
