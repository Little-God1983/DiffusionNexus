using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// One concrete execution engine (ComfyUI, local inference, …) that knows how to report
/// readiness for the features it serves. <see cref="IFeatureBackendRouter"/> picks the
/// right backend per feature; the chosen backend then answers
/// <see cref="CheckFeatureAsync"/> on its own terms.
/// </summary>
public interface IFeatureBackend
{
    /// <summary>The engine this backend represents.</summary>
    BackendKind Kind { get; }

    /// <summary>Human-readable name shown to users (e.g. <c>"ComfyUI"</c>).</summary>
    string DisplayName { get; }

    /// <summary>
    /// Checks whether the given feature is ready to execute on this backend.
    /// </summary>
    /// <remarks>
    /// Cancellation contract: cancellation requested via the caller's token must propagate as
    /// <see cref="OperationCanceledException"/> rather than being reported through
    /// <see cref="FeatureReadinessResult.MissingRequirements"/>. The caller cancelling is not
    /// evidence the backend is unavailable, so implementations must rethrow when the caller's
    /// token requested the cancellation, instead of mapping the exception onto a fabricated
    /// readiness failure. An <see cref="OperationCanceledException"/> that does not originate
    /// from the caller's token (e.g. an internal HTTP timeout) is not caller cancellation and
    /// must not be rethrown as one (issue #434).
    /// </remarks>
    /// <param name="feature">The feature to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FeatureReadinessResult> CheckFeatureAsync(Feature feature, CancellationToken ct = default);
}
