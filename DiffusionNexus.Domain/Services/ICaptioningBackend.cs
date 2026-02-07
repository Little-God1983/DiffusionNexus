namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Common abstraction for image captioning backends.
/// Implementations may use local inference (LlamaSharp) or remote services (ComfyUI).
/// </summary>
public interface ICaptioningBackend
{
    /// <summary>
    /// Human-readable name of this backend (e.g. "Local Inference", "ComfyUI – Qwen3-VL").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Checks whether the backend is currently available and ready to generate captions.
    /// For local inference this means the native library is loaded and a model is downloaded.
    /// For ComfyUI this means the server is reachable.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the backend can accept captioning requests.</returns>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Generates a caption for a single image.
    /// </summary>
    /// <param name="imagePath">Absolute path to the image file.</param>
    /// <param name="prompt">The prompt/instruction to guide caption generation.</param>
    /// <param name="triggerWord">Optional token to prepend to the generated caption.</param>
    /// <param name="blacklistedWords">Words to remove from the generated caption.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The captioning result.</returns>
    Task<CaptioningResult> GenerateSingleCaptionAsync(
        string imagePath,
        string prompt,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null,
        CancellationToken ct = default);

    /// <summary>
    /// Generates captions for a batch of images, reporting progress along the way.
    /// </summary>
    /// <param name="config">The captioning job configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result for each image in the batch.</returns>
    Task<IReadOnlyList<CaptioningResult>> GenerateBatchCaptionsAsync(
        CaptioningJobConfig config,
        IProgress<CaptioningProgress>? progress = null,
        CancellationToken ct = default);
}
