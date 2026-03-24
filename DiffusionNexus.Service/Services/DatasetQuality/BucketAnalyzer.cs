using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Simulates kohya_ss-style image bucketing for LoRA training datasets.
/// Generates resolution buckets, assigns images to the best-matching bucket,
/// computes distribution metrics, and detects quality issues.
/// </summary>
public sealed class BucketAnalyzer
{
    /// <summary>Check name used on all generated <see cref="Issue"/> records.</summary>
    public const string CheckName = "Bucket Analysis";

    /// <summary>Scale factor threshold above which upscaling is flagged as critical.</summary>
    public const double UpscaleCriticalThreshold = 2.0;

    /// <summary>Crop percentage above which cropping is flagged as critical.</summary>
    public const double CropCriticalThreshold = 30.0;

    /// <summary>Crop percentage above which cropping is flagged as a warning.</summary>
    public const double CropWarningThreshold = 15.0;

    /// <summary>Proportion above which one bucket is considered dominant.</summary>
    internal const double DominantBucketThreshold = 0.6;

    /// <summary>Ratio of (max resolution / min resolution) above which variance is flagged.</summary>
    internal const double ResolutionVarianceThreshold = 4.0;

    private readonly IImageDimensionReader _dimensionReader;

    /// <summary>
    /// Creates a new <see cref="BucketAnalyzer"/>.
    /// </summary>
    /// <param name="dimensionReader">Reader used to obtain image dimensions from headers.</param>
    public BucketAnalyzer(IImageDimensionReader dimensionReader)
    {
        ArgumentNullException.ThrowIfNull(dimensionReader);
        _dimensionReader = dimensionReader;
    }

    #region Bucket Generation

    /// <summary>
    /// Generates the set of resolution buckets using the kohya_ss algorithm.
    /// For each candidate width (in step increments between min and max), the height
    /// is calculated as <c>floor(targetArea / width / step) × step</c>. Duplicate
    /// resolutions are eliminated and both landscape/portrait orientations are included.
    /// </summary>
    /// <param name="config">Bucket configuration.</param>
    /// <returns>Sorted list of unique bucket resolutions.</returns>
    public static IReadOnlyList<BucketResolution> GenerateBuckets(BucketConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        long targetArea = (long)config.BaseResolution * config.BaseResolution;
        var buckets = new HashSet<BucketResolution>();

        for (int width = config.MinDimension; width <= config.MaxDimension; width += config.StepSize)
        {
            int height = (int)(targetArea / width / config.StepSize) * config.StepSize;

            if (height < config.MinDimension || height > config.MaxDimension)
                continue;

            double ar = (double)Math.Max(width, height) / Math.Min(width, height);
            if (ar > config.MaxAspectRatio)
                continue;

            // Add both orientations (HashSet deduplicates the square case)
            buckets.Add(new BucketResolution(width, height));
            buckets.Add(new BucketResolution(height, width));
        }

        var sorted = buckets.ToList();
        sorted.Sort();
        return sorted;
    }

    #endregion

    #region Image Assignment

    /// <summary>
    /// Finds the best-matching bucket for an image based on closest aspect ratio,
    /// with pixel-area tiebreaking.
    /// </summary>
    /// <param name="imageWidth">Image width in pixels.</param>
    /// <param name="imageHeight">Image height in pixels.</param>
    /// <param name="buckets">Available buckets to choose from.</param>
    /// <returns>The best matching bucket.</returns>
    public static BucketResolution FindBestBucket(
        int imageWidth, int imageHeight, IReadOnlyList<BucketResolution> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0)
            throw new ArgumentException("At least one bucket is required.", nameof(buckets));

        double imageAr = (double)imageWidth / imageHeight;
        long imageArea = (long)imageWidth * imageHeight;

        BucketResolution best = buckets[0];
        double bestDiff = double.MaxValue;
        long bestAreaDiff = long.MaxValue;

        for (int i = 0; i < buckets.Count; i++)
        {
            double bucketAr = buckets[i].AspectRatio;
            double diff = Math.Abs(imageAr - bucketAr);
            long areaDiff = Math.Abs(imageArea - buckets[i].PixelCount);

            if (diff < bestDiff || (Math.Abs(diff - bestDiff) < 1e-9 && areaDiff < bestAreaDiff))
            {
                best = buckets[i];
                bestDiff = diff;
                bestAreaDiff = areaDiff;
            }
        }

        return best;
    }

    /// <summary>
    /// Calculates the scale factor and crop percentage for fitting an image into a bucket.
    /// The image is scaled so the bucket is completely covered, then excess pixels are cropped.
    /// </summary>
    /// <param name="imageWidth">Original image width.</param>
    /// <param name="imageHeight">Original image height.</param>
    /// <param name="bucket">Target bucket resolution.</param>
    /// <returns>Tuple of (scaleFactor, cropPercentage).</returns>
    public static (double ScaleFactor, double CropPercentage) CalculateFitMetrics(
        int imageWidth, int imageHeight, BucketResolution bucket)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || bucket.Width <= 0 || bucket.Height <= 0)
            return (0, 0);

        // Scale so the bucket is fully covered (no letterboxing)
        double scaleX = (double)bucket.Width / imageWidth;
        double scaleY = (double)bucket.Height / imageHeight;
        double scaleFactor = Math.Max(scaleX, scaleY);

        double resizedWidth = imageWidth * scaleFactor;
        double resizedHeight = imageHeight * scaleFactor;
        double resizedArea = resizedWidth * resizedHeight;
        double bucketArea = (double)bucket.Width * bucket.Height;

        double cropPercentage = resizedArea > 0
            ? (1.0 - bucketArea / resizedArea) * 100.0
            : 0;

        return (scaleFactor, Math.Max(0, cropPercentage));
    }

    #endregion

    #region Distribution Score

    /// <summary>
    /// Calculates the Shannon Evenness Index for the bucket distribution (0–100).
    /// A score of 100 means images are perfectly evenly distributed across buckets.
    /// A score of 0 means all images are in a single bucket.
    /// </summary>
    /// <param name="distribution">Non-empty distribution entries.</param>
    /// <returns>Evenness score 0–100.</returns>
    public static double CalculateDistributionScore(IReadOnlyList<BucketDistributionEntry> distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);

        if (distribution.Count <= 1)
            return 0;

        int total = 0;
        for (int i = 0; i < distribution.Count; i++)
            total += distribution[i].ImageCount;

        if (total == 0)
            return 0;

        double h = 0;
        for (int i = 0; i < distribution.Count; i++)
        {
            if (distribution[i].ImageCount <= 0)
                continue;

            double p = (double)distribution[i].ImageCount / total;
            h -= p * Math.Log(p);
        }

        double hMax = Math.Log(distribution.Count);
        if (hMax <= 0)
            return 0;

        return Math.Round(h / hMax * 100.0, 1);
    }

    /// <summary>
    /// Returns a human-readable label for a distribution score.
    /// </summary>
    public static string GetScoreLabel(double score) => score switch
    {
        >= 80 => "Excellent",
        >= 60 => "Good",
        >= 40 => "Fair",
        _ => "Poor"
    };

    #endregion

    #region Folder Analysis

    /// <summary>
    /// Scans a folder for image files and reads their dimensions.
    /// </summary>
    /// <param name="folderPath">Absolute path to the dataset folder.</param>
    /// <returns>List of image files with valid dimensions.</returns>
    public List<ImageFileInfo> ScanFolder(string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        if (!Directory.Exists(folderPath))
            return [];

        var images = new List<ImageFileInfo>();

        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            if (!SupportedMediaTypes.IsImageFile(file))
                continue;

            var dims = _dimensionReader.ReadDimensions(file);
            if (dims.IsValid)
                images.Add(new ImageFileInfo(file, dims.Width, dims.Height));
        }

        return images;
    }

    /// <summary>
    /// Runs the full bucket analysis pipeline on a folder.
    /// </summary>
    /// <param name="folderPath">Absolute path to the dataset folder.</param>
    /// <param name="config">Bucket generation configuration.</param>
    /// <returns>Complete analysis result.</returns>
    public BucketAnalysisResult AnalyzeFolder(string folderPath, BucketConfig config)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(config);

        var images = ScanFolder(folderPath);
        return Analyze(images, config);
    }

    #endregion

    #region Core Analysis

    /// <summary>
    /// Runs bucket analysis on a list of pre-loaded images.
    /// This is the main entry point for both production use and unit testing.
    /// </summary>
    /// <param name="images">Image file info list.</param>
    /// <param name="config">Bucket configuration.</param>
    /// <returns>Complete analysis result.</returns>
    public static BucketAnalysisResult Analyze(IReadOnlyList<ImageFileInfo> images, BucketConfig config)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(config);

        var buckets = GenerateBuckets(config);

        if (images.Count == 0)
        {
            return new BucketAnalysisResult
            {
                AllBuckets = buckets,
                Distribution = [],
                Assignments = [],
                Issues = [],
                DistributionScore = 0,
                ScoreLabel = GetScoreLabel(0)
            };
        }

        // Assign each image to its best bucket
        var assignments = new List<ImageBucketAssignment>(images.Count);
        var bucketGroups = new Dictionary<BucketResolution, List<string>>();

        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];

            if (img.Width <= 0 || img.Height <= 0)
                continue;

            var bestBucket = FindBestBucket(img.Width, img.Height, buckets);
            var (scaleFactor, cropPercentage) = CalculateFitMetrics(img.Width, img.Height, bestBucket);

            assignments.Add(new ImageBucketAssignment
            {
                FilePath = img.FilePath,
                FileName = Path.GetFileName(img.FilePath),
                OriginalWidth = img.Width,
                OriginalHeight = img.Height,
                AssignedBucket = bestBucket,
                CropPercentage = Math.Round(cropPercentage, 1),
                ScaleFactor = Math.Round(scaleFactor, 3)
            });

            if (!bucketGroups.TryGetValue(bestBucket, out var paths))
            {
                paths = [];
                bucketGroups[bestBucket] = paths;
            }
            paths.Add(img.FilePath);
        }

        // Build distribution (sorted by bucket)
        var distribution = bucketGroups
            .Select(kvp => new BucketDistributionEntry
            {
                Bucket = kvp.Key,
                ImageCount = kvp.Value.Count,
                ImagePaths = kvp.Value
            })
            .OrderBy(d => d.Bucket)
            .ToList();

        // Detect issues
        var issues = new List<Issue>();
        DetectUpscaleIssues(assignments, issues);
        DetectCropIssues(assignments, issues);
        DetectSingleImageBuckets(distribution, config, issues);
        DetectDominantBucket(distribution, assignments.Count, issues);
        DetectTooManyBuckets(distribution, assignments.Count, issues);
        DetectResolutionVariance(images, issues);

        // Score
        double score = CalculateDistributionScore(distribution);
        string label = GetScoreLabel(score);

        return new BucketAnalysisResult
        {
            AllBuckets = buckets,
            Distribution = distribution,
            Assignments = assignments,
            Issues = issues,
            DistributionScore = score,
            ScoreLabel = label
        };
    }

    #endregion

    #region Issue Detection

    private static void DetectUpscaleIssues(List<ImageBucketAssignment> assignments, List<Issue> issues)
    {
        var critical = assignments
            .Where(a => a.ScaleFactor >= UpscaleCriticalThreshold)
            .ToList();

        if (critical.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{critical.Count} image(s) require ≥{UpscaleCriticalThreshold:F1}× upscaling — significant quality loss expected.",
                Details = "Images much smaller than the target bucket need heavy upscaling, which introduces artifacts. "
                        + "Consider using higher-resolution source images or a smaller base resolution.",
                Domain = CheckDomain.Image,
                CheckName = CheckName,
                AffectedFiles = critical.Select(a => a.FilePath).ToList()
            });
        }
    }

    private static void DetectCropIssues(List<ImageBucketAssignment> assignments, List<Issue> issues)
    {
        var critical = assignments.Where(a => a.CropPercentage >= CropCriticalThreshold).ToList();
        var warnings = assignments.Where(a => a.CropPercentage >= CropWarningThreshold && a.CropPercentage < CropCriticalThreshold).ToList();

        if (critical.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{critical.Count} image(s) lose ≥{CropCriticalThreshold:F0}% of pixels to cropping.",
                Details = "Extreme cropping discards significant portions of the image. The subject may be cut off. "
                        + "Consider pre-cropping these images to better match a training bucket.",
                Domain = CheckDomain.Image,
                CheckName = CheckName,
                AffectedFiles = critical.Select(a => a.FilePath).ToList()
            });
        }

        if (warnings.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{warnings.Count} image(s) lose {CropWarningThreshold:F0}–{CropCriticalThreshold:F0}% of pixels to cropping.",
                Details = "Moderate cropping may cut off parts of the subject. Review these images for proper framing.",
                Domain = CheckDomain.Image,
                CheckName = CheckName,
                AffectedFiles = warnings.Select(a => a.FilePath).ToList()
            });
        }
    }

    private static void DetectSingleImageBuckets(
        List<BucketDistributionEntry> distribution, BucketConfig config, List<Issue> issues)
    {
        if (config.BatchSize <= 1)
            return;

        var singles = distribution.Where(d => d.ImageCount == 1).ToList();
        if (singles.Count > 0)
        {
            var bucketLabels = string.Join(", ", singles.Select(s => s.Bucket.Label));
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{singles.Count} bucket(s) contain only 1 image — incomplete batches with batch size {config.BatchSize}.",
                Details = $"Buckets with fewer images than the batch size cause padding or repeated images during training. "
                        + $"Affected buckets: {bucketLabels}.",
                Domain = CheckDomain.Image,
                CheckName = CheckName,
                AffectedFiles = singles.SelectMany(s => s.ImagePaths).ToList()
            });
        }
    }

    private static void DetectDominantBucket(
        List<BucketDistributionEntry> distribution, int totalImages, List<Issue> issues)
    {
        if (totalImages == 0 || distribution.Count <= 1)
            return;

        var dominant = distribution
            .FirstOrDefault(d => (double)d.ImageCount / totalImages >= DominantBucketThreshold);

        if (dominant is not null)
        {
            double pct = (double)dominant.ImageCount / totalImages * 100;
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"Bucket {dominant.Bucket.Label} contains {pct:F0}% of all images — dataset is heavily skewed.",
                Details = "A dominant bucket means the model trains disproportionately on one aspect ratio. "
                        + "Consider diversifying image aspect ratios for better generalization.",
                Domain = CheckDomain.Image,
                CheckName = CheckName,
                AffectedFiles = dominant.ImagePaths.ToList()
            });
        }
    }

    private static void DetectTooManyBuckets(
        List<BucketDistributionEntry> distribution, int totalImages, List<Issue> issues)
    {
        if (totalImages == 0)
            return;

        // Heuristic: more used buckets than half the images is likely too fragmented
        if (distribution.Count > totalImages / 2 && distribution.Count > 3)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{distribution.Count} buckets used for {totalImages} images — distribution may be too fragmented.",
                Details = "When images are spread across many buckets, each bucket has very few samples. "
                        + "This can reduce training stability. Consider standardizing image aspect ratios.",
                Domain = CheckDomain.Image,
                CheckName = CheckName
            });
        }
    }

    private static void DetectResolutionVariance(IReadOnlyList<ImageFileInfo> images, List<Issue> issues)
    {
        if (images.Count < 2)
            return;

        long minArea = long.MaxValue;
        long maxArea = long.MinValue;

        for (int i = 0; i < images.Count; i++)
        {
            if (images[i].Width <= 0 || images[i].Height <= 0)
                continue;

            long area = (long)images[i].Width * images[i].Height;
            if (area < minArea) minArea = area;
            if (area > maxArea) maxArea = area;
        }

        if (minArea <= 0 || maxArea <= 0)
            return;

        double ratio = (double)maxArea / minArea;
        if (ratio >= ResolutionVarianceThreshold)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"Image resolution varies by {ratio:F1}× (pixel area) — consider normalizing.",
                Details = "Large resolution differences mean some images are heavily upscaled while others are "
                        + "downscaled. This inconsistency can hurt training quality.",
                Domain = CheckDomain.Image,
                CheckName = CheckName
            });
        }
    }

    #endregion
}
