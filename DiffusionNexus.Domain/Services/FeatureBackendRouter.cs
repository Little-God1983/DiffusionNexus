using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Default <see cref="IFeatureBackendRouter"/> that uses a static per-feature constant map
/// to pick a backend. Re-pointing a feature at a different backend later is a one-line
/// change to <see cref="DefaultRouting"/> — no view-model edits. A user-supplied
/// <see cref="BackendKind"/> override (from the readiness panel's backend picker) takes
/// precedence over the default map.
/// </summary>
public sealed class FeatureBackendRouter : IFeatureBackendRouter
{
    /// <summary>
    /// The v1 routing policy: every feature defaults to ComfyUI. Local-inference backends are
    /// wired in and become reachable when the user picks them in the backend picker.
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
        var ordered = new List<IFeatureBackend>();
        foreach (var backend in backends)
        {
            // Last write wins — DI registration order determines the active backend per kind,
            // which lets the host swap in a stub for tests without removing the production
            // registration.
            if (!byKind.ContainsKey(backend.Kind))
                ordered.Add(backend);
            byKind[backend.Kind] = backend;
        }

        _backendsByKind = byKind;
        _routing = routing ?? DefaultRouting;
        AvailableBackends = ordered;
    }

    /// <inheritdoc />
    public IReadOnlyList<IFeatureBackend> AvailableBackends { get; }

    /// <inheritdoc />
    public IFeatureBackend? Resolve(Feature feature) => Resolve(feature, null);

    /// <inheritdoc />
    public IFeatureBackend? Resolve(Feature feature, BackendKind? preferredKind)
    {
        // An explicit user pick wins over the default map; if that backend isn't registered the
        // caller gets null and surfaces a "no backend" readiness result.
        if (preferredKind is { } kind)
            return _backendsByKind.GetValueOrDefault(kind);

        if (!_routing.TryGetValue(feature, out var defaultKind))
            return null;

        return _backendsByKind.GetValueOrDefault(defaultKind);
    }

    /// <inheritdoc />
    public BackendKind? GetDefaultKind(Feature feature)
        => _routing.TryGetValue(feature, out var kind) ? kind : null;
}
