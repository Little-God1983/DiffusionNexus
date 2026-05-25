using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Default <see cref="IFeatureReadinessService"/> implementation. Delegates each check to the
/// <see cref="IFeatureBackend"/> selected by the <see cref="IFeatureBackendRouter"/>.
/// </summary>
public sealed class FeatureReadinessService : IFeatureReadinessService
{
    private readonly IFeatureBackendRouter _router;
    private readonly Func<Feature, FeatureRequirements?> _requirementsLookup;

    public FeatureReadinessService(
        IFeatureBackendRouter router,
        Func<Feature, FeatureRequirements?> requirementsLookup)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(requirementsLookup);
        _router = router;
        _requirementsLookup = requirementsLookup;
    }

    /// <inheritdoc />
    public async Task<FeatureReadinessResult> CheckAsync(Feature feature, CancellationToken ct = default)
    {
        var backend = _router.Resolve(feature);

        if (backend is null)
        {
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = BackendKind.ComfyUI,
                IsBackendOnline = false,
                IsReady = false,
                ActiveBackendName = "(none)",
                MissingRequirements = [$"No backend is registered for feature '{feature}'."],
                Warnings = []
            };
        }

        return await backend.CheckFeatureAsync(feature, ct);
    }

    /// <inheritdoc />
    public FeatureRequirements? GetRequirements(Feature feature) => _requirementsLookup(feature);
}
