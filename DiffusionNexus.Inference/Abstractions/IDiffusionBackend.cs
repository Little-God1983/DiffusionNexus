using DiffusionNexus.Inference.Models;

namespace DiffusionNexus.Inference.Abstractions;

/// <summary>
/// Resolves <see cref="ModelDescriptor"/> instances from the local model library.
/// Implementations walk the configured ComfyUI-layout folders to discover what's available.
/// </summary>
public interface IModelCatalog
{
    /// <summary>
    /// Returns every model the catalog can describe with files actually present on disk.
    /// </summary>
    IReadOnlyList<ModelDescriptor> ListAvailable();

    /// <summary>
    /// Returns the descriptor with the given key, or <c>null</c> if no satisfying file set exists on disk.
    /// </summary>
    ModelDescriptor? TryGet(string key);
}

/// <summary>
/// The diffusion-engine seam. A single contract that both the local
/// <c>StableDiffusionCppBackend</c> and (in a follow-up) a ComfyUI adapter satisfy, so the
/// canvas ViewModel never depends on a concrete engine.
/// </summary>
public interface IDiffusionBackend
{
    /// <summary>Human-readable name of this backend (e.g. <c>"Diffusion Nexus Core"</c>).</summary>
    string DisplayName { get; }

    /// <summary>The catalog of models this backend can run.</summary>
    IModelCatalog Catalog { get; }

    /// <summary>
    /// Human-readable descriptions of requirements that are not currently satisfied.
    /// Populated after <see cref="IsAvailableAsync"/> returns <c>false</c>. Backends with no
    /// additional requirements may always return an empty list.
    /// </summary>
    IReadOnlyList<string> MissingRequirements { get; }

    /// <summary>
    /// Non-blocking warnings discovered during <see cref="IsAvailableAsync"/>.
    /// </summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Checks whether this diffusion backend is currently available and ready to generate.
    /// Implementations should populate <see cref="MissingRequirements"/> on a <c>false</c> result.
    /// </summary>
    /// <remarks>
    /// Cancellation contract: cancellation requested via the caller's token must propagate as
    /// <see cref="OperationCanceledException"/> rather than being reported through
    /// <see cref="MissingRequirements"/>. The caller cancelling is not evidence the backend is
    /// unavailable, so implementations must rethrow when the caller's token requested the
    /// cancellation, instead of mapping the exception onto a fabricated readiness failure. An
    /// <see cref="OperationCanceledException"/> that does not originate from the caller's token
    /// (e.g. an internal HTTP timeout) is not caller cancellation and must not be rethrown as one
    /// (issue #434).
    /// </remarks>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs a single generation, streaming progress and yielding the final <see cref="DiffusionResult"/>
    /// inside the last item (where <c>Result</c> is non-null and <c>Progress.Phase == Completed</c>).
    /// </summary>
    /// <remarks>
    /// The token is observed between phases. <b>Mid-sampling cancellation is not supported in v1</b>
    /// because <c>stable-diffusion.cpp</c> does not expose a cancel hook (TODO(v2-cancel)).
    /// </remarks>
    IAsyncEnumerable<DiffusionStreamItem> GenerateAsync(
        DiffusionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One item in the streaming generation channel. Either carries a progress update, or
/// (on the final item) the completed <see cref="DiffusionResult"/>.
/// </summary>
public sealed record DiffusionStreamItem(DiffusionProgress Progress, DiffusionResult? Result = null);
