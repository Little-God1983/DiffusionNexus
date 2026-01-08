namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of an image upscaling operation.
/// </summary>
/// <param name="Success">Whether the operation completed successfully.</param>
/// <param name="ImageData">The upscaled image as PNG bytes.</param>
/// <param name="Width">Width of the upscaled image in pixels.</param>
/// <param name="Height">Height of the upscaled image in pixels.</param>
/// <param name="ErrorMessage">Error message if the operation failed.</param>
public record ImageUpscalingResult(
    bool Success,
    byte[]? ImageData,
    int Width,
    int Height,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static ImageUpscalingResult Succeeded(byte[] imageData, int width, int height) =>
        new(true, imageData, width, height, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ImageUpscalingResult Failed(string error) =>
        new(false, null, 0, 0, error);
}

/// <summary>
/// Progress information for upscaling operations.
/// </summary>
/// <param name="Phase">Current processing phase.</param>
/// <param name="Message">Human-readable status message.</param>
/// <param name="Percentage">Progress percentage (0-100), -1 if indeterminate.</param>
public record UpscalingProgress(UpscalingPhase Phase, string Message, int Percentage = -1);

/// <summary>
/// Phases of the upscaling process.
/// </summary>
public enum UpscalingPhase
{
    /// <summary>Preparing image for processing.</summary>
    Preparing,
    
    /// <summary>Processing tiles through AI model.</summary>
    ProcessingTiles,
    
    /// <summary>Stitching tiles back together.</summary>
    Stitching,
    
    /// <summary>Resizing to target scale.</summary>
    ResizingToTarget,
    
    /// <summary>Finalizing output.</summary>
    Finalizing
}

/// <summary>
/// Service for upscaling images using the 4x-UltraSharp ONNX model.
/// Runs inference locally using GPU acceleration when available.
/// </summary>
public interface IImageUpscalingService : IDisposable
{
    /// <summary>
    /// Gets the current status of the ONNX model.
    /// </summary>
    ModelStatus GetModelStatus();

    /// <summary>
    /// Gets the path where the model is stored.
    /// </summary>
    string GetModelPath();

    /// <summary>
    /// Downloads the 4x-UltraSharp model.
    /// </summary>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded.</returns>
    Task<bool> DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upscales an image using AI enhancement with variable target scale.
    /// </summary>
    /// <remarks>
    /// The AI model always produces a 4x upscaled image internally.
    /// If <paramref name="targetScale"/> is less than 4.0, the result is
    /// downscaled to the target dimensions using high-quality Lanczos3 resampling.
    /// </remarks>
    /// <param name="imageData">Raw RGBA image data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="targetScale">Target scale factor (1.1 to 4.0).</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the upscaled image.</returns>
    Task<ImageUpscalingResult> UpscaleImageAsync(
        byte[] imageData,
        int width,
        int height,
        float targetScale,
        IProgress<UpscalingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the ONNX inference session.
    /// Call this before UpscaleImageAsync for faster first-time inference.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if initialization succeeded.</returns>
    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether GPU acceleration is available.
    /// </summary>
    bool IsGpuAvailable { get; }

    /// <summary>
    /// Gets whether the service is currently processing an image.
    /// </summary>
    bool IsProcessing { get; }
}
