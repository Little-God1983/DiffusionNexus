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
    /// Human-readable descriptions of requirements that are not currently satisfied.
    /// Populated after <see cref="IsAvailableAsync"/> returns <c>false</c>.
    /// Backends that have no additional requirements may always return an empty list.
    /// </summary>
    IReadOnlyList<string> MissingRequirements { get; }

    /// <summary>
    /// Non-blocking warnings discovered during <see cref="IsAvailableAsync"/>.
    /// Unlike <see cref="MissingRequirements"/>, warnings do not prevent the backend from
    /// being used — they inform the user about conditions that may affect the first run
    /// (e.g. a large model download that will happen on first execution).
    /// </summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Checks whether the backend is currently available and ready to generate captions.
    /// For local inference this means the native library is loaded and a model is downloaded.
    /// For ComfyUI this means the server is reachable and all required custom nodes are installed.
    /// Implementations should populate <see cref="MissingRequirements"/> when returning <c>false</c>.
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
    /// <param name="temperature">Inference temperature (0.0–2.0). Lower values are more deterministic.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The captioning result.</returns>
    Task<CaptioningResult> GenerateSingleCaptionAsync(
        string imagePath,
        string prompt,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null,
        float temperature = 0.7f,
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
