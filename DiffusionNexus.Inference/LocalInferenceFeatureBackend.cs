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

    public LocalInferenceFeatureBackend(
        ICaptioningBackend? captioning = null,
        IDiffusionBackend? diffusion = null)
    {
        _captioning = captioning;
        _diffusion = diffusion;
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

                // Image-generation features. Today none of these are routed to the local backend
                // by FeatureBackendRouter.DefaultRouting, but the wiring is in place so a future
                // router change can flip Outpaint / Inpainting / BatchUpscale to local without
                // touching view-models.
                Feature.Inpainting or
                Feature.Outpaint or
                Feature.OutpaintVision or
                Feature.BatchUpscale or
                Feature.BatchUpscaleVision => await CheckDiffusionAsync(feature, ct),

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
        if (_diffusion is null)
        {
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = Kind,
                IsBackendOnline = false,
                IsReady = false,
                ActiveBackendName = DisplayName,
                MissingRequirements = ["Local diffusion backend is not registered."],
                Warnings = []
            };
        }

        var available = await _diffusion.IsAvailableAsync(ct);

        return new FeatureReadinessResult
        {
            Feature = feature,
            Backend = Kind,
            IsBackendOnline = available || _diffusion.MissingRequirements.Count == 0,
            IsReady = available,
            ActiveBackendName = _diffusion.DisplayName,
            MissingRequirements = _diffusion.MissingRequirements,
            Warnings = _diffusion.Warnings
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
}
