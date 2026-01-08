namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of a background removal operation.
/// </summary>
/// <param name="Success">Whether the operation completed successfully.</param>
/// <param name="MaskData">The alpha mask data (grayscale, same dimensions as input).</param>
/// <param name="Width">Width of the mask in pixels.</param>
/// <param name="Height">Height of the mask in pixels.</param>
/// <param name="ErrorMessage">Error message if the operation failed.</param>
public record BackgroundRemovalResult(
    bool Success,
    byte[]? MaskData,
    int Width,
    int Height,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static BackgroundRemovalResult Succeeded(byte[] maskData, int width, int height) =>
        new(true, maskData, width, height, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static BackgroundRemovalResult Failed(string error) =>
        new(false, null, 0, 0, error);
}

/// <summary>
/// Status of the ONNX model used for background removal.
/// </summary>
public enum ModelStatus
{
    /// <summary>Model is not downloaded.</summary>
    NotDownloaded,
    
    /// <summary>Model is currently being downloaded.</summary>
    Downloading,
    
    /// <summary>Model is ready for use.</summary>
    Ready,
    
    /// <summary>Model file exists but may be corrupted.</summary>
    Corrupted
}

/// <summary>
/// Progress information for model download.
/// </summary>
/// <param name="BytesDownloaded">Number of bytes downloaded so far.</param>
/// <param name="TotalBytes">Total size of the model file (-1 if unknown).</param>
/// <param name="Status">Current download status message.</param>
public record ModelDownloadProgress(long BytesDownloaded, long TotalBytes, string Status)
{
    /// <summary>
    /// Download progress as a percentage (0-100).
    /// Returns -1 if total size is unknown.
    /// </summary>
    public double Percentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : -1;
}

/// <summary>
/// Service for removing backgrounds from images using the RMBG-1.4 ONNX model.
/// Runs inference locally using GPU acceleration when available.
/// </summary>
public interface IBackgroundRemovalService : IDisposable
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
    /// Downloads the RMBG-1.4 model from HuggingFace.
    /// </summary>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded.</returns>
    Task<bool> DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the background from an image.
    /// </summary>
    /// <param name="imageData">Raw RGBA image data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the alpha mask.</returns>
    Task<BackgroundRemovalResult> RemoveBackgroundAsync(
        byte[] imageData,
        int width,
        int height,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the ONNX inference session.
    /// Call this before RemoveBackgroundAsync for faster first-time inference.
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
