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
    public const int DimensionMultiple = 8;

    /// <summary>
    /// Tolerance for aspect ratio matching. If an image's ratio is within this
    /// tolerance of a bucket ratio, it's considered a match (no adjustment needed).
    /// </summary>
    public const double AspectRatioTolerance = 0.01;

    /// <summary>
    /// Represents a crop result with the target dimensions and crop offsets.
    /// </summary>
    public record CropResult(
        int TargetWidth,
        int TargetHeight,
        int CropX,
        int CropY,
        BucketDefinition Bucket);

    /// <summary>
    /// Represents a padding result with the target dimensions and padding offsets.
    /// </summary>
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
    public void SetAllowedBuckets(IEnumerable<BucketDefinition>? buckets)
    {
        _allowedBuckets = buckets == null || !buckets.Any() ? DefaultBuckets : buckets;
    }

    /// <summary>
    /// Calculates the crop parameters for an image to fit the nearest aspect ratio bucket.
    /// </summary>
    public CropResult CalculateCrop(int sourceWidth, int sourceHeight)
    {
        ValidateDimensions(sourceWidth, sourceHeight);

        double sourceRatio = (double)sourceWidth / sourceHeight;

        if (IsAlreadyValidBucket(sourceWidth, sourceHeight, sourceRatio, out var existingBucket))
        {
            return new CropResult(sourceWidth, sourceHeight, 0, 0, existingBucket!);
        }

        var bestBucket = FindBestBucket(sourceWidth, sourceHeight, sourceRatio, FitMode.Crop);
        var (targetWidth, targetHeight) = CalculateCropDimensions(sourceWidth, sourceHeight, bestBucket.Ratio);

        targetWidth = RoundDownToMultiple(Math.Min(targetWidth, sourceWidth));
        targetHeight = RoundDownToMultiple(Math.Min(targetHeight, sourceHeight));

        int cropX = (sourceWidth - targetWidth) / 2;
        int cropY = (sourceHeight - targetHeight) / 2;

        return new CropResult(targetWidth, targetHeight, cropX, cropY, bestBucket);
    }

    /// <summary>
    /// Calculates the padding parameters for an image to fit the nearest aspect ratio bucket.
    /// </summary>
    public PadResult CalculatePad(int sourceWidth, int sourceHeight)
    {
        ValidateDimensions(sourceWidth, sourceHeight);

        double sourceRatio = (double)sourceWidth / sourceHeight;

        if (IsAlreadyValidBucket(sourceWidth, sourceHeight, sourceRatio, out var existingBucket))
        {
            return new PadResult(sourceWidth, sourceHeight, 0, 0, existingBucket!);
        }

        var bestBucket = FindBestBucket(sourceWidth, sourceHeight, sourceRatio, FitMode.Pad);
        var (canvasWidth, canvasHeight) = CalculatePadDimensions(sourceWidth, sourceHeight, bestBucket.Ratio);

        canvasWidth = RoundUpToMultiple(Math.Max(canvasWidth, sourceWidth));
        canvasHeight = RoundUpToMultiple(Math.Max(canvasHeight, sourceHeight));

        int imageX = (canvasWidth - sourceWidth) / 2;
        int imageY = (canvasHeight - sourceHeight) / 2;

        return new PadResult(canvasWidth, canvasHeight, imageX, imageY, bestBucket);
    }

    private static void ValidateDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
    }

    private bool IsAlreadyValidBucket(int width, int height, double ratio, out BucketDefinition? bucket)
    {
        bucket = null;

        if (width % DimensionMultiple != 0 || height % DimensionMultiple != 0)
            return false;

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
    /// Rounds down to the nearest multiple of DimensionMultiple (8).
    /// </summary>
    public static int RoundDownToMultiple(int value) => (value / DimensionMultiple) * DimensionMultiple;

    /// <summary>
    /// Rounds up to the nearest multiple of DimensionMultiple (8).
    /// </summary>
    public static int RoundUpToMultiple(int value) => ((value + DimensionMultiple - 1) / DimensionMultiple) * DimensionMultiple;

    /// <summary>
    /// Unified bucket selection algorithm that minimizes pixel change based on fit mode.
    /// </summary>
    private BucketDefinition FindBestBucket(int sourceWidth, int sourceHeight, double sourceRatio, FitMode mode)
    {
        var bestBucket = _allowedBuckets.First();
        int minChange = int.MaxValue;
        int sourceArea = sourceWidth * sourceHeight;

        foreach (var bucket in _allowedBuckets)
        {
            int change = mode == FitMode.Crop
                ? CalculateCropPixelLoss(sourceWidth, sourceHeight, sourceArea, bucket.Ratio)
                : CalculatePadPixelAddition(sourceWidth, sourceHeight, sourceArea, bucket.Ratio);

            if (change < minChange || (change == minChange && IsBetterRatioMatch(sourceRatio, bucket, bestBucket)))
            {
                minChange = change;
                bestBucket = bucket;
            }
        }

        return bestBucket;
    }

    private static int CalculateCropPixelLoss(int sourceWidth, int sourceHeight, int sourceArea, double targetRatio)
    {
        var (w, h) = CalculateCropDimensions(sourceWidth, sourceHeight, targetRatio);
        return sourceArea - (RoundDownToMultiple(w) * RoundDownToMultiple(h));
    }

    private static int CalculatePadPixelAddition(int sourceWidth, int sourceHeight, int sourceArea, double targetRatio)
    {
        var (w, h) = CalculatePadDimensions(sourceWidth, sourceHeight, targetRatio);
        return (RoundUpToMultiple(w) * RoundUpToMultiple(h)) - sourceArea;
    }

    private static bool IsBetterRatioMatch(double sourceRatio, BucketDefinition candidate, BucketDefinition current)
    {
        return Math.Abs(sourceRatio - candidate.Ratio) < Math.Abs(sourceRatio - current.Ratio);
    }

    private static (int Width, int Height) CalculateCropDimensions(int sourceWidth, int sourceHeight, double targetRatio)
    {
        double sourceRatio = (double)sourceWidth / sourceHeight;

        int targetWidth, targetHeight;
        if (sourceRatio > targetRatio)
        {
            targetHeight = sourceHeight;
            targetWidth = (int)(sourceHeight * targetRatio);
        }
        else
        {
            targetWidth = sourceWidth;
            targetHeight = (int)(sourceWidth / targetRatio);
        }

        return (Math.Min(targetWidth, sourceWidth), Math.Min(targetHeight, sourceHeight));
    }

    private static (int Width, int Height) CalculatePadDimensions(int sourceWidth, int sourceHeight, double targetRatio)
    {
        double sourceRatio = (double)sourceWidth / sourceHeight;

        int canvasWidth, canvasHeight;
        if (sourceRatio > targetRatio)
        {
            canvasWidth = sourceWidth;
            canvasHeight = (int)(sourceWidth / targetRatio);
        }
        else
        {
            canvasHeight = sourceHeight;
            canvasWidth = (int)(sourceHeight * targetRatio);
        }

        return (Math.Max(canvasWidth, sourceWidth), Math.Max(canvasHeight, sourceHeight));
    }
}
