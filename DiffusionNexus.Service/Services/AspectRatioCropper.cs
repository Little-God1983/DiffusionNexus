using DiffusionNexus.Domain.Autocropper;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Provides aspect ratio adjustment calculations for LoRA training image preparation.
/// Supports both cropping (removing pixels) and padding (adding canvas).
/// Supports standard training buckets: 16:9, 9:16, 1:1, 4:3, 3:4, 5:4, 4:5.
/// Dimensions are rounded to multiples of 8 for training compatibility.
/// </summary>
public sealed class AspectRatioCropper
{
    /// <summary>
    /// Dimensions are rounded down to this multiple for training compatibility.
    /// Standard value is 8 (used by most diffusion models).
    /// </summary>
    private const int DimensionMultiple = 8;

    /// <summary>
    /// Tolerance for aspect ratio matching. If an image's ratio is within this
    /// tolerance of a bucket ratio, it's considered a match (no adjustment needed).
    /// </summary>
    private const double AspectRatioTolerance = 0.01;

    /// <summary>
    /// Represents a crop result with the target dimensions and crop offsets.
    /// </summary>
    /// <param name="TargetWidth">Target width after cropping.</param>
    /// <param name="TargetHeight">Target height after cropping.</param>
    /// <param name="CropX">X offset for center crop.</param>
    /// <param name="CropY">Y offset for center crop.</param>
    /// <param name="Bucket">The bucket definition used for cropping.</param>
    public record CropResult(
        int TargetWidth,
        int TargetHeight,
        int CropX,
        int CropY,
        BucketDefinition Bucket);

    /// <summary>
    /// Represents a padding result with the target dimensions and padding offsets.
    /// </summary>
    /// <param name="CanvasWidth">Total canvas width (including padding).</param>
    /// <param name="CanvasHeight">Total canvas height (including padding).</param>
    /// <param name="ImageX">X offset where the original image is placed on the canvas.</param>
    /// <param name="ImageY">Y offset where the original image is placed on the canvas.</param>
    /// <param name="Bucket">The bucket definition used for padding.</param>
    public record PadResult(
        int CanvasWidth,
        int CanvasHeight,
        int ImageX,
        int ImageY,
        BucketDefinition Bucket);

    private static readonly List<BucketDefinition> DefaultBuckets =
    [
        new() { Name = "16:9", Width = 16, Height = 9 },
        new() { Name = "9:16", Width = 9, Height = 16 },
        new() { Name = "1:1", Width = 1, Height = 1 },
        new() { Name = "4:3", Width = 4, Height = 3 },
        new() { Name = "3:4", Width = 3, Height = 4 },
        new() { Name = "5:4", Width = 5, Height = 4 },
        new() { Name = "4:5", Width = 4, Height = 5 }
    ];

    private IEnumerable<BucketDefinition> _allowedBuckets = DefaultBuckets;

    /// <summary>
    /// Sets the allowed buckets for adjustment. If null or empty, all buckets are used.
    /// </summary>
    /// <param name="buckets">The buckets to allow, or null/empty for all default buckets.</param>
    public void SetAllowedBuckets(IEnumerable<BucketDefinition>? buckets)
    {
        if (buckets == null || !buckets.Any())
        {
            _allowedBuckets = DefaultBuckets;
        }
        else
        {
            _allowedBuckets = buckets;
        }
    }

    /// <summary>
    /// Calculates the crop parameters for an image to fit the nearest aspect ratio bucket.
    /// Crops as little as possible while maintaining the original quality (no scaling).
    /// Dimensions are rounded to multiples of 8 for training compatibility.
    /// </summary>
    /// <param name="sourceWidth">Original image width in pixels.</param>
    /// <param name="sourceHeight">Original image height in pixels.</param>
    /// <returns>Crop result with target dimensions and center-crop offsets.</returns>
    public CropResult CalculateCrop(int sourceWidth, int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);

        double sourceRatio = (double)sourceWidth / sourceHeight;

        // Check if already a valid bucket (dimensions are multiples of 8 and ratio matches)
        if (IsAlreadyValidBucket(sourceWidth, sourceHeight, sourceRatio, out var existingBucket))
        {
            return new CropResult(sourceWidth, sourceHeight, 0, 0, existingBucket!);
        }

        // Find the nearest bucket with minimal pixel loss (for cropping)
        var bestBucket = FindBestBucketForCrop(sourceWidth, sourceHeight, sourceRatio);

        // Calculate crop dimensions that maintain the target aspect ratio
        var (targetWidth, targetHeight) = CalculateCropDimensions(
            sourceWidth, sourceHeight, bestBucket.Ratio);

        // Round dimensions down to nearest multiple of 8
        targetWidth = RoundDownToMultiple(targetWidth);
        targetHeight = RoundDownToMultiple(targetHeight);

        // Ensure we don't exceed source dimensions
        targetWidth = Math.Min(targetWidth, sourceWidth);
        targetHeight = Math.Min(targetHeight, sourceHeight);

        // Center the crop
        int cropX = (sourceWidth - targetWidth) / 2;
        int cropY = (sourceHeight - targetHeight) / 2;

        return new CropResult(targetWidth, targetHeight, cropX, cropY, bestBucket);
    }

    /// <summary>
    /// Calculates the padding parameters for an image to fit the nearest aspect ratio bucket.
    /// Adds canvas to preserve the entire image while achieving the target aspect ratio.
    /// Dimensions are rounded to multiples of 8 for training compatibility.
    /// </summary>
    /// <param name="sourceWidth">Original image width in pixels.</param>
    /// <param name="sourceHeight">Original image height in pixels.</param>
    /// <returns>Pad result with canvas dimensions and image placement offsets.</returns>
    public PadResult CalculatePad(int sourceWidth, int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);

        double sourceRatio = (double)sourceWidth / sourceHeight;

        // Check if already a valid bucket (dimensions are multiples of 8 and ratio matches)
        if (IsAlreadyValidBucket(sourceWidth, sourceHeight, sourceRatio, out var existingBucket))
        {
            return new PadResult(sourceWidth, sourceHeight, 0, 0, existingBucket!);
        }

        // Find the nearest bucket with minimal canvas addition (for padding)
        var bestBucket = FindBestBucketForPad(sourceWidth, sourceHeight, sourceRatio);

        // Calculate canvas dimensions that maintain the target aspect ratio
        var (canvasWidth, canvasHeight) = CalculatePadDimensions(
            sourceWidth, sourceHeight, bestBucket.Ratio);

        // Round dimensions up to nearest multiple of 8
        canvasWidth = RoundUpToMultiple(canvasWidth);
        canvasHeight = RoundUpToMultiple(canvasHeight);

        // Ensure canvas is at least as large as source
        canvasWidth = Math.Max(canvasWidth, sourceWidth);
        canvasHeight = Math.Max(canvasHeight, sourceHeight);

        // Center the image on the canvas
        int imageX = (canvasWidth - sourceWidth) / 2;
        int imageY = (canvasHeight - sourceHeight) / 2;

        return new PadResult(canvasWidth, canvasHeight, imageX, imageY, bestBucket);
    }

    /// <summary>
    /// Checks if the image is already a valid bucket (no adjustment needed).
    /// </summary>
    private bool IsAlreadyValidBucket(int width, int height, double ratio, out BucketDefinition? bucket)
    {
        bucket = null;

        // Check if dimensions are already multiples of 8
        if (width % DimensionMultiple != 0 || height % DimensionMultiple != 0)
        {
            return false;
        }

        // Check if ratio matches any allowed bucket within tolerance
        foreach (var b in _allowedBuckets)
        {
            if (Math.Abs(ratio - b.Ratio) <= AspectRatioTolerance)
            {
                bucket = b;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rounds a dimension down to the nearest multiple of DimensionMultiple (8).
    /// </summary>
    private static int RoundDownToMultiple(int value)
    {
        return (value / DimensionMultiple) * DimensionMultiple;
    }

    /// <summary>
    /// Rounds a dimension up to the nearest multiple of DimensionMultiple (8).
    /// </summary>
    private static int RoundUpToMultiple(int value)
    {
        return ((value + DimensionMultiple - 1) / DimensionMultiple) * DimensionMultiple;
    }

    private BucketDefinition FindBestBucketForCrop(int sourceWidth, int sourceHeight, double sourceRatio)
    {
        var bestBucket = _allowedBuckets.First();
        int minPixelLoss = int.MaxValue;

        foreach (var bucket in _allowedBuckets)
        {
            // Calculate what the final dimensions would be for this bucket
            var (cropWidth, cropHeight) = CalculateFinalCropDimensions(
                sourceWidth, sourceHeight, bucket.Ratio);

            int pixelLoss = (sourceWidth * sourceHeight) - (cropWidth * cropHeight);

            // Prefer bucket with minimal pixel loss
            if (pixelLoss < minPixelLoss)
            {
                minPixelLoss = pixelLoss;
                bestBucket = bucket;
            }
            // If equal loss, prefer bucket closer to source ratio
            else if (pixelLoss == minPixelLoss)
            {
                double currentDiff = Math.Abs(sourceRatio - bestBucket.Ratio);
                double newDiff = Math.Abs(sourceRatio - bucket.Ratio);
                if (newDiff < currentDiff)
                {
                    bestBucket = bucket;
                }
            }
        }

        return bestBucket;
    }

    private BucketDefinition FindBestBucketForPad(int sourceWidth, int sourceHeight, double sourceRatio)
    {
        var bestBucket = _allowedBuckets.First();
        int minCanvasAddition = int.MaxValue;

        foreach (var bucket in _allowedBuckets)
        {
            // Calculate what the canvas dimensions would be for this bucket
            var (canvasWidth, canvasHeight) = CalculateFinalPadDimensions(
                sourceWidth, sourceHeight, bucket.Ratio);

            int canvasAddition = (canvasWidth * canvasHeight) - (sourceWidth * sourceHeight);

            // Prefer bucket with minimal canvas addition
            if (canvasAddition < minCanvasAddition)
            {
                minCanvasAddition = canvasAddition;
                bestBucket = bucket;
            }
            // If equal addition, prefer bucket closer to source ratio
            else if (canvasAddition == minCanvasAddition)
            {
                double currentDiff = Math.Abs(sourceRatio - bestBucket.Ratio);
                double newDiff = Math.Abs(sourceRatio - bucket.Ratio);
                if (newDiff < currentDiff)
                {
                    bestBucket = bucket;
                }
            }
        }

        return bestBucket;
    }

    /// <summary>
    /// Calculates the final dimensions after cropping and rounding to multiples of 8.
    /// </summary>
    private static (int Width, int Height) CalculateFinalCropDimensions(
        int sourceWidth, int sourceHeight, double targetRatio)
    {
        var (width, height) = CalculateCropDimensions(sourceWidth, sourceHeight, targetRatio);
        return (RoundDownToMultiple(width), RoundDownToMultiple(height));
    }

    /// <summary>
    /// Calculates the final canvas dimensions after padding and rounding to multiples of 8.
    /// </summary>
    private static (int Width, int Height) CalculateFinalPadDimensions(
        int sourceWidth, int sourceHeight, double targetRatio)
    {
        var (width, height) = CalculatePadDimensions(sourceWidth, sourceHeight, targetRatio);
        return (RoundUpToMultiple(width), RoundUpToMultiple(height));
    }

    private static (int Width, int Height) CalculateCropDimensions(
        int sourceWidth, int sourceHeight, double targetRatio)
    {
        double sourceRatio = (double)sourceWidth / sourceHeight;

        int targetWidth, targetHeight;

        if (sourceRatio > targetRatio)
        {
            // Image is wider than target - crop width
            targetHeight = sourceHeight;
            targetWidth = (int)(sourceHeight * targetRatio);
        }
        else
        {
            // Image is taller than target - crop height
            targetWidth = sourceWidth;
            targetHeight = (int)(sourceWidth / targetRatio);
        }

        // Ensure dimensions don't exceed source
        targetWidth = Math.Min(targetWidth, sourceWidth);
        targetHeight = Math.Min(targetHeight, sourceHeight);

        return (targetWidth, targetHeight);
    }

    private static (int Width, int Height) CalculatePadDimensions(
        int sourceWidth, int sourceHeight, double targetRatio)
    {
        double sourceRatio = (double)sourceWidth / sourceHeight;

        int canvasWidth, canvasHeight;

        if (sourceRatio > targetRatio)
        {
            // Image is wider than target - add vertical padding
            canvasWidth = sourceWidth;
            canvasHeight = (int)(sourceWidth / targetRatio);
        }
        else
        {
            // Image is taller than target - add horizontal padding
            canvasHeight = sourceHeight;
            canvasWidth = (int)(sourceHeight * targetRatio);
        }

        // Ensure canvas is at least as large as source
        canvasWidth = Math.Max(canvasWidth, sourceWidth);
        canvasHeight = Math.Max(canvasHeight, sourceHeight);

        return (canvasWidth, canvasHeight);
    }
}
