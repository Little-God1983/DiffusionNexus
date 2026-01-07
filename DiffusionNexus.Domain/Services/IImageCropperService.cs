using DiffusionNexus.Domain.Autocropper;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of scanning a folder for image files.
/// </summary>
/// <param name="TotalFiles">Total number of files in the folder.</param>
/// <param name="ImageFiles">Number of image files found.</param>
/// <param name="ImagePaths">Array of full paths to image files.</param>
public record FolderScanResult(int TotalFiles, int ImageFiles, string[] ImagePaths);

/// <summary>
/// Progress information for the cropping operation.
/// </summary>
/// <param name="ProcessedCount">Number of images processed so far.</param>
/// <param name="TotalCount">Total number of images to process.</param>
/// <param name="CurrentFile">Name of the file currently being processed.</param>
/// <param name="CurrentBucket">The bucket the current file is being cropped to.</param>
public record CropProgress(
    int ProcessedCount,
    int TotalCount,
    string CurrentFile,
    BucketDefinition? CurrentBucket);

/// <summary>
/// Result of the cropping operation.
/// </summary>
/// <param name="SuccessCount">Number of images successfully processed.</param>
/// <param name="FailedCount">Number of images that failed processing.</param>
/// <param name="SkippedCount">Number of images skipped (already correct size).</param>
/// <param name="Duration">Total duration of the operation.</param>
public record CropOperationResult(
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    TimeSpan Duration);

/// <summary>
/// Service for cropping images to standard aspect ratio buckets for LoRA training.
/// </summary>
public interface IImageCropperService
{
    /// <summary>
    /// Scans a folder and returns metadata about files and images found.
    /// </summary>
    /// <param name="folderPath">Path to the folder to scan.</param>
    /// <returns>Scan result with file counts and image paths.</returns>
    FolderScanResult ScanFolder(string folderPath);

    /// <summary>
    /// Processes all images in the source folder, cropping them to the nearest standard bucket.
    /// Images are first cropped to aspect ratio, then optionally downscaled.
    /// </summary>
    /// <param name="sourceFolderPath">Source folder containing images.</param>
    /// <param name="targetFolderPath">Optional target folder. If null, images are overwritten in place.</param>
    /// <param name="allowedBuckets">Optional array of allowed buckets. If null or empty, all buckets are used.</param>
    /// <param name="maxLongestSide">Optional maximum size for the longest side. If null, no scaling is performed.</param>
    /// <param name="skipUnchanged">If true, files that don't need cropping or scaling are skipped (not copied/overwritten).</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the cropping operation.</returns>
    Task<CropOperationResult> ProcessImagesAsync(
        string sourceFolderPath,
        string? targetFolderPath,
        IEnumerable<BucketDefinition>? allowedBuckets = null,
        int? maxLongestSide = null,
        bool skipUnchanged = false,
        IProgress<CropProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
