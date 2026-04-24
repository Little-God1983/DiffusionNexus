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
    /// <summary>The catalog of models this backend can run.</summary>
    IModelCatalog Catalog { get; }

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
