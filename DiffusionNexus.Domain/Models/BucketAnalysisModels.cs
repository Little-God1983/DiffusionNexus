using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Configuration for kohya_ss-style bucket generation.
/// Controls the resolution grid that training images are assigned to.
/// </summary>
public record BucketConfig
{
    /// <summary>
    /// Base resolution in pixels (e.g. 512, 768, 1024).
    /// The target pixel area is <c>BaseResolution²</c>.
    /// </summary>
    public int BaseResolution { get; init; } = 1024;

    /// <summary>
    /// Step size for width/height increments. Both dimensions are multiples of this value.
    /// </summary>
    public int StepSize { get; init; } = 64;

    /// <summary>
    /// Minimum allowed dimension (width or height) in pixels.
    /// </summary>
    public int MinDimension { get; init; } = 256;

    /// <summary>
    /// Maximum allowed dimension (width or height) in pixels.
    /// </summary>
    public int MaxDimension { get; init; } = 2048;

    /// <summary>
    /// Maximum aspect ratio allowed (width / height or height / width).
    /// Buckets exceeding this ratio are excluded.
    /// </summary>
    public double MaxAspectRatio { get; init; } = 2.0;

    /// <summary>
    /// Training batch size — used for informational warnings about single-image buckets.
    /// </summary>
    public int BatchSize { get; init; } = 1;
}

/// <summary>
/// A single bucket resolution produced by the generation algorithm.
/// </summary>
/// <param name="Width">Width in pixels (always a multiple of <see cref="BucketConfig.StepSize"/>).</param>
/// <param name="Height">Height in pixels (always a multiple of <see cref="BucketConfig.StepSize"/>).</param>
public readonly record struct BucketResolution(int Width, int Height) : IComparable<BucketResolution>
{
    /// <summary>
    /// Human-readable label (e.g. "1024 × 768").
    /// </summary>
    public string Label => $"{Width} × {Height}";

    /// <summary>
    /// Aspect ratio as width / height.
    /// </summary>
    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;

    /// <summary>
    /// Total pixel count.
    /// </summary>
    public long PixelCount => (long)Width * Height;

    /// <inheritdoc />
    public int CompareTo(BucketResolution other)
    {
        int cmp = Width.CompareTo(other.Width);
        return cmp != 0 ? cmp : Height.CompareTo(other.Height);
    }
}

/// <summary>
/// Dimensions read from an image file header (no pixel decode).
/// </summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
public readonly record struct ImageDimensions(int Width, int Height)
{
    /// <summary>
    /// True when both dimensions are positive.
    /// </summary>
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// Basic info about an image file in the dataset.
/// </summary>
/// <param name="FilePath">Absolute path to the image.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public record ImageFileInfo(string FilePath, int Width, int Height);

/// <summary>
/// Result of assigning a single image to its best-matching bucket.
/// </summary>
public record ImageBucketAssignment
{
    /// <summary>Absolute file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>File name only (for display).</summary>
    public required string FileName { get; init; }

    /// <summary>Original width in pixels.</summary>
    public required int OriginalWidth { get; init; }

    /// <summary>Original height in pixels.</summary>
    public required int OriginalHeight { get; init; }

    /// <summary>The bucket this image was assigned to.</summary>
    public required BucketResolution AssignedBucket { get; init; }

    /// <summary>
    /// Percentage of pixels that would be cropped after scaling to fit the bucket.
    /// 0 means the image perfectly matches the bucket aspect ratio.
    /// </summary>
    public required double CropPercentage { get; init; }

    /// <summary>
    /// Scale factor applied to fit the image into the bucket.
    /// Values &gt; 1.0 indicate upscaling; values &lt; 1.0 indicate downscaling.
    /// </summary>
    public required double ScaleFactor { get; init; }
}

/// <summary>
/// Distribution entry showing how many images are assigned to a given bucket.
/// </summary>
public record BucketDistributionEntry
{
    /// <summary>The bucket resolution.</summary>
    public required BucketResolution Bucket { get; init; }

    /// <summary>Number of images assigned to this bucket.</summary>
    public required int ImageCount { get; init; }

    /// <summary>Paths of images in this bucket.</summary>
    public required IReadOnlyList<string> ImagePaths { get; init; }
}

/// <summary>
/// Complete result of a bucket analysis run.
/// </summary>
public record BucketAnalysisResult
{
    /// <summary>All generated buckets (even those with zero images).</summary>
    public required IReadOnlyList<BucketResolution> AllBuckets { get; init; }

    /// <summary>Distribution of images across buckets (only buckets with ≥ 1 image).</summary>
    public required IReadOnlyList<BucketDistributionEntry> Distribution { get; init; }

    /// <summary>Per-image assignment details.</summary>
    public required IReadOnlyList<ImageBucketAssignment> Assignments { get; init; }

    /// <summary>Issues detected during analysis.</summary>
    public required IReadOnlyList<Issue> Issues { get; init; }

    /// <summary>
    /// Distribution evenness score (0–100). Higher is more evenly distributed.
    /// Uses the Shannon Evenness Index.
    /// </summary>
    public required double DistributionScore { get; init; }

    /// <summary>
    /// Human-readable label for the score (Poor / Fair / Good / Excellent).
    /// </summary>
    public required string ScoreLabel { get; init; }
}
