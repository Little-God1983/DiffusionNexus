using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of a single image caption generation.
/// </summary>
/// <param name="Success">Whether the caption was generated successfully.</param>
/// <param name="ImagePath">Path to the source image.</param>
/// <param name="Caption">The generated caption text.</param>
/// <param name="CaptionFilePath">Path where the caption was saved.</param>
/// <param name="ErrorMessage">Error message if generation failed.</param>
/// <param name="WasSkipped">Whether the image was skipped (e.g., existing caption, invalid file).</param>
/// <param name="SkipReason">Reason for skipping if WasSkipped is true.</param>
public record CaptioningResult(
    bool Success,
    string ImagePath,
    string? Caption = null,
    string? CaptionFilePath = null,
    string? ErrorMessage = null,
    bool WasSkipped = false,
    string? SkipReason = null)
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static CaptioningResult Succeeded(string imagePath, string caption, string captionFilePath) =>
        new(true, imagePath, caption, captionFilePath);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static CaptioningResult Failed(string imagePath, string error) =>
        new(false, imagePath, ErrorMessage: error);

    /// <summary>
    /// Creates a skipped result.
    /// </summary>
    public static CaptioningResult Skipped(string imagePath, string reason) =>
        new(true, imagePath, WasSkipped: true, SkipReason: reason);
}

/// <summary>
/// Progress information for a captioning batch job.
/// </summary>
/// <param name="CurrentIndex">Index of the current image being processed (0-based).</param>
/// <param name="TotalCount">Total number of images to process.</param>
/// <param name="CurrentImagePath">Path of the image currently being processed.</param>
/// <param name="Status">Current status message.</param>
/// <param name="LastResult">The result of the last processed image.</param>
public record CaptioningProgress(
    int CurrentIndex,
    int TotalCount,
    string CurrentImagePath,
    string Status,
    CaptioningResult? LastResult = null)
{
    /// <summary>
    /// Progress as a percentage (0-100).
    /// </summary>
    public double Percentage => TotalCount > 0 ? (double)CurrentIndex / TotalCount * 100 : 0;

    /// <summary>
    /// Number of images completed.
    /// </summary>
    public int CompletedCount => CurrentIndex;
}

/// <summary>
/// Status of a captioning model.
/// </summary>
public enum CaptioningModelStatus
{
    /// <summary>Model is not downloaded.</summary>
    NotDownloaded,

    /// <summary>Model is currently being downloaded.</summary>
    Downloading,

    /// <summary>Model is downloaded and ready for use.</summary>
    Ready,

    /// <summary>Model file exists but may be corrupted or incomplete.</summary>
    Corrupted,

    /// <summary>Model is currently loaded in memory.</summary>
    Loaded
}

/// <summary>
/// Information about a captioning model.
/// </summary>
/// <param name="ModelType">The model type.</param>
/// <param name="Status">Current status of the model.</param>
/// <param name="FilePath">Local file path of the model.</param>
/// <param name="FileSizeBytes">Size of the model file in bytes (0 if not downloaded).</param>
/// <param name="ExpectedSizeBytes">Expected size of the model file in bytes.</param>
/// <param name="DisplayName">Human-readable name of the model.</param>
/// <param name="Description">Description of the model's capabilities.</param>
public record CaptioningModelInfo(
    CaptioningModelType ModelType,
    CaptioningModelStatus Status,
    string FilePath,
    long FileSizeBytes,
    long ExpectedSizeBytes,
    string DisplayName,
    string Description);

/// <summary>
/// Service for generating image captions using local vision-language models.
/// Uses LlamaSharp with CUDA 12 backend for NVIDIA GPU acceleration.
/// </summary>
public interface ICaptioningService : IDisposable
{
    /// <summary>
    /// Gets whether the service is currently processing images.
    /// </summary>
    bool IsProcessing { get; }

    /// <summary>
    /// Gets whether a model is currently loaded and ready for inference.
    /// </summary>
    bool IsModelLoaded { get; }

    /// <summary>
    /// Gets the currently loaded model type, if any.
    /// </summary>
    CaptioningModelType? LoadedModelType { get; }

    /// <summary>
    /// Gets whether GPU acceleration is available.
    /// </summary>
    bool IsGpuAvailable { get; }

    /// <summary>
    /// Gets information about a specific model.
    /// </summary>
    /// <param name="modelType">The model type to query.</param>
    /// <returns>Information about the model.</returns>
    CaptioningModelInfo GetModelInfo(CaptioningModelType modelType);

    /// <summary>
    /// Gets information about all supported models.
    /// </summary>
    /// <returns>List of model information.</returns>
    IReadOnlyList<CaptioningModelInfo> GetAllModels();

    /// <summary>
    /// Downloads a model from HuggingFace.
    /// </summary>
    /// <param name="modelType">The model to download.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded.</returns>
    Task<bool> DownloadModelAsync(
        CaptioningModelType modelType,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a model into memory for inference.
    /// </summary>
    /// <param name="modelType">The model to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if loading succeeded.</returns>
    Task<bool> LoadModelAsync(
        CaptioningModelType modelType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads the currently loaded model from memory.
    /// </summary>
    void UnloadModel();

    /// <summary>
    /// Generates captions for a batch of images.
    /// </summary>
    /// <param name="config">The captioning job configuration.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of results for each image.</returns>
    Task<IReadOnlyList<CaptioningResult>> GenerateCaptionsAsync(
        CaptioningJobConfig config,
        IProgress<CaptioningProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a caption for a single image.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="systemPrompt">The system prompt for caption generation.</param>
    /// <param name="triggerWord">Optional trigger word to prepend.</param>
    /// <param name="blacklistedWords">Words to filter from the result.</param>
    /// <param name="temperature">Inference temperature.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captioning result.</returns>
    Task<CaptioningResult> GenerateSingleCaptionAsync(
        string imagePath,
        string systemPrompt,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null,
        float temperature = 0.7f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a downloaded model file.
    /// </summary>
    /// <param name="modelType">The model to delete.</param>
    void DeleteModel(CaptioningModelType modelType);
}
