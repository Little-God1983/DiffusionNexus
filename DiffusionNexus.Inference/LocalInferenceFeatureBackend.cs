using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Inference.Abstractions;
using Serilog;

namespace DiffusionNexus.Inference;

/// <summary>
/// <see cref="IFeatureBackend"/> for features that execute in-process via the local inference
/// stack (LlamaSharp captioning, stable-diffusion.cpp generation). Wraps the existing
/// <c>IsAvailableAsync / MissingRequirements / Warnings</c> surface that
/// <see cref="ICaptioningBackend"/> and <see cref="IDiffusionBackend"/> already expose, so
/// readiness funnels through the same contract the rest of the system uses.
/// </summary>
public sealed class LocalInferenceFeatureBackend : IFeatureBackend
{
    private static readonly ILogger Logger = Log.ForContext<LocalInferenceFeatureBackend>();

    private readonly ICaptioningBackend? _captioning;
    private readonly IDiffusionBackend? _diffusion;
    private readonly Func<CancellationToken, Task<IDiffusionBackend?>>? _diffusionAccessor;

    public LocalInferenceFeatureBackend(
        ICaptioningBackend? captioning = null,
        IDiffusionBackend? diffusion = null,
        Func<CancellationToken, Task<IDiffusionBackend?>>? diffusionAccessor = null)
    {
        _captioning = captioning;
        _diffusion = diffusion;
        // The local diffusion backend is built lazily (it probes ComfyUI installs + the native lib),
        // so accept an async accessor that resolves it on demand rather than forcing eager construction.
        _diffusionAccessor = diffusionAccessor;
    }

    /// <inheritdoc />
    public BackendKind Kind => BackendKind.LocalInference;

    /// <inheritdoc />
    public string DisplayName => "Diffusion Nexus Core";

    /// <inheritdoc />
    public async Task<FeatureReadinessResult> CheckFeatureAsync(Feature feature, CancellationToken ct = default)
    {
        try
        {
            return feature switch
            {
                Feature.Captioning => await CheckCaptioningAsync(feature, ct),

                // Inpainting has a real local execution path (the Image Editor's Inpaint tool routes
                // to the stable-diffusion.cpp backend), so report its true model readiness.
                Feature.Inpainting => await CheckDiffusionAsync(feature, ct),

                // The other image features expose the backend picker but DON'T yet have a local
                // execution path — they still run on ComfyUI. Report "not available locally" so the
                // picker is honest (picking the local engine greys out Generate) instead of showing
                // "Ready" for an engine that never runs the job.
                Feature.Outpaint or
                Feature.OutpaintVision or
                Feature.BatchUpscale or
                Feature.BatchUpscaleVision => LocalExecutionNotWired(feature),

                _ => NotSupported(feature)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Local inference readiness check failed for {Feature}", feature);
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = Kind,
                IsBackendOnline = false,
                IsReady = false,
                ActiveBackendName = DisplayName,
                MissingRequirements = [$"Local readiness check failed: {ex.Message}"],
                Warnings = []
            };
        }
    }

    private async Task<FeatureReadinessResult> CheckCaptioningAsync(Feature feature, CancellationToken ct)
    {
        if (_captioning is null)
        {
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = Kind,
                IsBackendOnline = false,
                IsReady = false,
                ActiveBackendName = DisplayName,
                MissingRequirements = ["Local captioning backend is not registered."],
                Warnings = []
            };
        }

        var available = await _captioning.IsAvailableAsync(ct);

        return new FeatureReadinessResult
        {
            Feature = feature,
            Backend = Kind,
            // For a local in-process backend "online" means the native library is loaded —
            // ICaptioningBackend.IsAvailableAsync already captures that. If IsAvailable is true
            // we treat the backend as online; otherwise the MissingRequirements list explains why.
            IsBackendOnline = available || _captioning.MissingRequirements.Count == 0,
            IsReady = available,
            ActiveBackendName = _captioning.DisplayName,
            MissingRequirements = _captioning.MissingRequirements,
            Warnings = _captioning.Warnings
        };
    }

    private async Task<FeatureReadinessResult> CheckDiffusionAsync(Feature feature, CancellationToken ct)
    {
        var diffusion = _diffusion;
        if (diffusion is null && _diffusionAccessor is not null)
            diffusion = await _diffusionAccessor(ct);

        if (diffusion is null)
        {
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = Kind,
                IsBackendOnline = false,
                IsReady = false,
                ActiveBackendName = DisplayName,
                MissingRequirements =
                    ["No local renderer is available — add a ComfyUI installation (its models folder is the local renderer's library)."],
                Warnings = []
            };
        }

        var available = await diffusion.IsAvailableAsync(ct);

        return new FeatureReadinessResult
        {
            Feature = feature,
            Backend = Kind,
            IsBackendOnline = available || diffusion.MissingRequirements.Count == 0,
            IsReady = available,
            ActiveBackendName = diffusion.DisplayName,
            MissingRequirements = diffusion.MissingRequirements,
            Warnings = diffusion.Warnings
        };
    }

    private FeatureReadinessResult NotSupported(Feature feature) => new()
    {
        Feature = feature,
        Backend = Kind,
        IsBackendOnline = false,
        IsReady = false,
        ActiveBackendName = DisplayName,
        MissingRequirements = [$"Local inference backend does not handle feature '{feature}'."],
        Warnings = []
    };

    /// <summary>
    /// Result for an image feature whose backend picker is exposed but whose local execution path
    /// isn't wired yet (only Inpainting is, currently). Reports not-ready so Generate greys out
    /// instead of silently running the job on ComfyUI.
    /// </summary>
    private FeatureReadinessResult LocalExecutionNotWired(Feature feature) => new()
    {
        Feature = feature,
        Backend = Kind,
        IsBackendOnline = false,
        IsReady = false,
        ActiveBackendName = DisplayName,
        MissingRequirements =
            [$"Local rendering for {feature} isn't available yet — switch to ComfyUI to run it, or use the Inpaint tool for local rendering."],
        Warnings = []
    };
}
