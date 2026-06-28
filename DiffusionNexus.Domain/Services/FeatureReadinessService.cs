using System.Linq;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Default <see cref="IFeatureReadinessService"/> implementation. Delegates each check to the
/// <see cref="IFeatureBackend"/> selected by the <see cref="IFeatureBackendRouter"/> (honouring
/// an explicit per-call backend override when the user has picked one).
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
    public Task<FeatureReadinessResult> CheckAsync(Feature feature, CancellationToken ct = default)
        => CheckAsync(feature, null, ct);

    /// <inheritdoc />
    public async Task<FeatureReadinessResult> CheckAsync(
        Feature feature,
        BackendKind? backendOverride,
        CancellationToken ct = default)
    {
        var backend = _router.Resolve(feature, backendOverride);

        if (backend is null)
        {
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = backendOverride ?? BackendKind.ComfyUI,
                IsBackendOnline = false,
                IsReady = false,
                ActiveBackendName = "(none)",
                MissingRequirements = backendOverride is { } kind
                    ? [$"The '{kind}' backend is not registered."]
                    : [$"No backend is registered for feature '{feature}'."],
                Warnings = []
            };
        }

        return await backend.CheckFeatureAsync(feature, ct);
    }

    /// <inheritdoc />
    public FeatureRequirements? GetRequirements(Feature feature) => _requirementsLookup(feature);

    /// <inheritdoc />
    public IReadOnlyList<BackendInfo> GetAvailableBackends()
        => _router.AvailableBackends
            .Select(b => new BackendInfo(b.Kind, b.DisplayName))
            .ToList();

    /// <inheritdoc />
    public BackendKind? GetDefaultBackend(Feature feature) => _router.GetDefaultKind(feature);
}
