using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Default <see cref="IFeatureBackendRouter"/> that uses a static per-feature constant map
/// to pick a backend. Re-pointing a feature at a different backend later is a one-line
/// change to <see cref="DefaultRouting"/> — no view-model edits.
/// </summary>
public sealed class FeatureBackendRouter : IFeatureBackendRouter
{
    /// <summary>
    /// The v1 routing policy: every feature currently runs on ComfyUI. Local-inference
    /// backends are wired in but not yet selected for any feature here.
    /// </summary>
    public static readonly IReadOnlyDictionary<Feature, BackendKind> DefaultRouting =
        new Dictionary<Feature, BackendKind>
        {
            [Feature.Captioning]         = BackendKind.ComfyUI,
            [Feature.Inpainting]         = BackendKind.ComfyUI,
            [Feature.BatchUpscale]       = BackendKind.ComfyUI,
            [Feature.BatchUpscaleVision] = BackendKind.ComfyUI,
            [Feature.Outpaint]           = BackendKind.ComfyUI,
            [Feature.OutpaintVision]     = BackendKind.ComfyUI,
        };

    private readonly IReadOnlyDictionary<BackendKind, IFeatureBackend> _backendsByKind;
    private readonly IReadOnlyDictionary<Feature, BackendKind> _routing;

    public FeatureBackendRouter(
        IEnumerable<IFeatureBackend> backends,
        IReadOnlyDictionary<Feature, BackendKind>? routing = null)
    {
        ArgumentNullException.ThrowIfNull(backends);

        var byKind = new Dictionary<BackendKind, IFeatureBackend>();
        foreach (var backend in backends)
        {
            // Last write wins — DI registration order determines the active backend per kind,
            // which lets the host swap in a stub for tests without removing the production
            // registration.
            byKind[backend.Kind] = backend;
        }

        _backendsByKind = byKind;
        _routing = routing ?? DefaultRouting;
    }

    /// <inheritdoc />
    public IFeatureBackend? Resolve(Feature feature)
    {
        if (!_routing.TryGetValue(feature, out var kind))
            return null;

        return _backendsByKind.GetValueOrDefault(kind);
    }
}
