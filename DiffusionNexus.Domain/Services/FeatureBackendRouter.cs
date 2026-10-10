using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Default <see cref="IFeatureBackendRouter"/> that uses a static per-feature constant map
/// to pick a backend, except for the features in <see cref="ServerModeFeatures"/>, which follow the
/// Settings → ComfyUI Server mode (read on every call; <see cref="ResolveAsync"/> reads it without
/// blocking). Re-pointing a feature at a different backend later is a one-line
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

    /// <summary>
    /// Features whose backend the Settings → ComfyUI Server dropdown decides (#606). OutpaintVision is
    /// included because the Outpaint panel runs both workflows through the same client; Batch Upscale
    /// (#608) likewise runs both its workflows through the client the dropdown picks.
    /// </summary>
    public static readonly IReadOnlySet<Feature> ServerModeFeatures =
        new HashSet<Feature>
        {
            Feature.Inpainting, Feature.Outpaint, Feature.OutpaintVision,
            Feature.BatchUpscale, Feature.BatchUpscaleVision,
        };

    private readonly Func<ComfyUiServerMode>? _serverMode;
    private readonly Func<CancellationToken, Task<ComfyUiServerMode>>? _serverModeAsync;
    private readonly IReadOnlyDictionary<BackendKind, IFeatureBackend> _backendsByKind;
    private readonly IReadOnlyDictionary<Feature, BackendKind> _routing;

    public FeatureBackendRouter(
        IEnumerable<IFeatureBackend> backends,
        IReadOnlyDictionary<Feature, BackendKind>? routing = null,
        Func<ComfyUiServerMode>? serverMode = null,
        Func<CancellationToken, Task<ComfyUiServerMode>>? serverModeAsync = null)
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
        _serverMode = serverMode;
        _serverModeAsync = serverModeAsync;
    }

    /// <inheritdoc />
    public IFeatureBackend? Resolve(Feature feature)
    {
        if (_serverMode is not null && ServerModeFeatures.Contains(feature))
            return ForMode(_serverMode());

        if (!_routing.TryGetValue(feature, out var staticKind))
            return null;

        return _backendsByKind.GetValueOrDefault(staticKind);
    }

    /// <inheritdoc />
    public async Task<IFeatureBackend?> ResolveAsync(Feature feature, CancellationToken ct = default)
    {
        if (_serverModeAsync is not null && ServerModeFeatures.Contains(feature))
            return ForMode(await _serverModeAsync(ct).ConfigureAwait(false));

        return Resolve(feature);
    }

    private IFeatureBackend? ForMode(ComfyUiServerMode mode) =>
        _backendsByKind.GetValueOrDefault(mode == ComfyUiServerMode.Engine ? BackendKind.Engine : BackendKind.ComfyUI);
}
