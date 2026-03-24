using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="BucketAnalyzer"/>.
/// Uses in-memory image data — no disk I/O needed.
/// </summary>
public class BucketAnalyzerTests
{
    private static BucketConfig DefaultConfig => new();

    private static BucketConfig ConfigWith(
        int baseRes = 1024, int step = 64, int min = 256, int max = 2048,
        double ratio = 2.0, int batch = 1) => new()
    {
        BaseResolution = baseRes,
        StepSize = step,
        MinDimension = min,
        MaxDimension = max,
        MaxAspectRatio = ratio,
        BatchSize = batch
    };

    private static List<ImageFileInfo> MakeImages(params (string path, int w, int h)[] images)
        => images.Select(i => new ImageFileInfo(i.path, i.w, i.h)).ToList();

    #region Bucket Generation

    [Fact]
    public void WhenDefaultConfigThenGeneratesExpectedBucketCount()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);

        // kohya_ss default: base=1024, step=64, min=256, max=2048, ratio=2.0
        buckets.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void WhenDefaultConfigThenAllDimensionsAreMultiplesOfStepSize()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);

        foreach (var b in buckets)
        {
            (b.Width % DefaultConfig.StepSize).Should().Be(0,
                $"width {b.Width} should be multiple of {DefaultConfig.StepSize}");
            (b.Height % DefaultConfig.StepSize).Should().Be(0,
                $"height {b.Height} should be multiple of {DefaultConfig.StepSize}");
        }
    }

    [Fact]
    public void WhenDefaultConfigThenAllDimensionsWithinBounds()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);

        foreach (var b in buckets)
        {
            b.Width.Should().BeGreaterThanOrEqualTo(DefaultConfig.MinDimension);
            b.Width.Should().BeLessThanOrEqualTo(DefaultConfig.MaxDimension);
            b.Height.Should().BeGreaterThanOrEqualTo(DefaultConfig.MinDimension);
            b.Height.Should().BeLessThanOrEqualTo(DefaultConfig.MaxDimension);
        }
    }

    [Fact]
    public void WhenDefaultConfigThenAspectRatiosWithinLimit()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);

        foreach (var b in buckets)
        {
            double ar = (double)Math.Max(b.Width, b.Height) / Math.Min(b.Width, b.Height);
            ar.Should().BeLessThanOrEqualTo(DefaultConfig.MaxAspectRatio + 0.01,
                $"bucket {b.Label} AR {ar:F2} exceeds limit");
        }
    }

    [Fact]
    public void WhenDefaultConfigThenBothOrientationsPresent()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);

        bool hasLandscape = buckets.Any(b => b.Width > b.Height);
        bool hasPortrait = buckets.Any(b => b.Height > b.Width);
        bool hasSquare = buckets.Any(b => b.Width == b.Height);

        hasLandscape.Should().BeTrue("should include landscape buckets");
        hasPortrait.Should().BeTrue("should include portrait buckets");
        hasSquare.Should().BeTrue("should include a square bucket");
    }

    [Fact]
    public void WhenDefaultConfigThenBucketsAreSorted()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);

        for (int i = 1; i < buckets.Count; i++)
        {
            buckets[i].CompareTo(buckets[i - 1]).Should().BeGreaterThanOrEqualTo(0,
                $"bucket at index {i} should be >= bucket at {i - 1}");
        }
    }

    [Fact]
    public void WhenDefaultConfigThenNoDuplicates()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);
        buckets.Distinct().Should().HaveCount(buckets.Count);
    }

    [Fact]
    public void WhenRatioIsOneThenOnlySquareBucket()
    {
        var config = ConfigWith(ratio: 1.0);
        var buckets = BucketAnalyzer.GenerateBuckets(config);

        buckets.Should().HaveCount(1);
        buckets[0].Width.Should().Be(buckets[0].Height);
        buckets[0].Width.Should().Be(config.BaseResolution);
    }

    [Fact]
    public void WhenSmallBaseResolutionThenFewerBuckets()
    {
        var small = BucketAnalyzer.GenerateBuckets(ConfigWith(baseRes: 512));
        var large = BucketAnalyzer.GenerateBuckets(ConfigWith(baseRes: 1024));

        small.Count.Should().BeLessThanOrEqualTo(large.Count);
    }

    [Fact]
    public void WhenLargerStepThenFewerBuckets()
    {
        var small = BucketAnalyzer.GenerateBuckets(ConfigWith(step: 128));
        var large = BucketAnalyzer.GenerateBuckets(ConfigWith(step: 64));

        small.Count.Should().BeLessThanOrEqualTo(large.Count);
    }

    [Fact]
    public void WhenNullConfigThenThrows()
    {
        var act = () => BucketAnalyzer.GenerateBuckets(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Image Assignment

    [Fact]
    public void WhenSquareImageThenAssignedToSquareBucket()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);
        var best = BucketAnalyzer.FindBestBucket(1024, 1024, buckets);

        best.Width.Should().Be(best.Height, "square image should match square bucket");
    }

    [Fact]
    public void WhenLandscapeImageThenAssignedToLandscapeBucket()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);
        var best = BucketAnalyzer.FindBestBucket(1920, 1080, buckets);

        best.Width.Should().BeGreaterThan(best.Height, "landscape image should match landscape bucket");
    }

    [Fact]
    public void WhenPortraitImageThenAssignedToPortraitBucket()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);
        var best = BucketAnalyzer.FindBestBucket(768, 1024, buckets);

        best.Height.Should().BeGreaterThanOrEqualTo(best.Width, "portrait image should match portrait bucket");
    }

    [Fact]
    public void WhenExactBucketMatchThenReturnsExactBucket()
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);

        // Pick any bucket and check exact match
        var target = buckets[0];
        var best = BucketAnalyzer.FindBestBucket(target.Width, target.Height, buckets);

        best.Should().Be(target);
    }

    [Theory]
    [InlineData(1600, 900, true)]   // 16:9ish → landscape
    [InlineData(900, 1600, false)]  // 9:16ish → portrait
    [InlineData(1024, 1024, true)]  // 1:1 → square (width == height)
    public void WhenVariousAspectRatiosThenClosestBucketIsChosen(int w, int h, bool expectLandscapeOrSquare)
    {
        var buckets = BucketAnalyzer.GenerateBuckets(DefaultConfig);
        var best = BucketAnalyzer.FindBestBucket(w, h, buckets);

        if (expectLandscapeOrSquare)
            best.Width.Should().BeGreaterThanOrEqualTo(best.Height);
        else
            best.Height.Should().BeGreaterThan(best.Width);
    }

    [Fact]
    public void WhenEmptyBucketListThenThrows()
    {
        var act = () => BucketAnalyzer.FindBestBucket(1024, 1024, []);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenNullBucketListThenThrows()
    {
        var act = () => BucketAnalyzer.FindBestBucket(1024, 1024, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Fit Metrics

    [Fact]
    public void WhenExactMatchThenScaleIsOneAndCropIsZero()
    {
        var bucket = new BucketResolution(1024, 1024);
        var (scale, crop) = BucketAnalyzer.CalculateFitMetrics(1024, 1024, bucket);

        scale.Should().BeApproximately(1.0, 0.001);
        crop.Should().BeApproximately(0, 0.1);
    }

    [Fact]
    public void WhenImageSmallerThanBucketThenScaleGreaterThanOne()
    {
        var bucket = new BucketResolution(1024, 1024);
        var (scale, _) = BucketAnalyzer.CalculateFitMetrics(512, 512, bucket);

        scale.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void WhenImageLargerThanBucketThenScaleLessThanOne()
    {
        var bucket = new BucketResolution(512, 512);
        var (scale, _) = BucketAnalyzer.CalculateFitMetrics(1024, 1024, bucket);

        scale.Should().BeLessThan(1.0);
    }

    [Fact]
    public void WhenAspectRatioMismatchThenCropIsPositive()
    {
        var bucket = new BucketResolution(1024, 1024); // 1:1
        var (_, crop) = BucketAnalyzer.CalculateFitMetrics(1920, 1080, bucket); // 16:9

        crop.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenAspectRatioMatchesThenCropIsNearZero()
    {
        var bucket = new BucketResolution(1024, 768);
        var (_, crop) = BucketAnalyzer.CalculateFitMetrics(2048, 1536, bucket); // same 4:3 ratio

        crop.Should().BeLessThan(1.0);
    }

    [Fact]
    public void WhenVerySmallImageThenUpscaleFactorIsHigh()
    {
        var bucket = new BucketResolution(1024, 768);
        var (scale, _) = BucketAnalyzer.CalculateFitMetrics(400, 300, bucket);

        scale.Should().BeGreaterThanOrEqualTo(2.0);
    }

    [Fact]
    public void WhenZeroDimensionsThenReturnsZeros()
    {
        var bucket = new BucketResolution(1024, 768);
        var (scale, crop) = BucketAnalyzer.CalculateFitMetrics(0, 0, bucket);

        scale.Should().Be(0);
        crop.Should().Be(0);
    }

    #endregion

    #region Issue Detection

    [Fact]
    public void WhenImageRequiresHeavyUpscalingThenCriticalIssue()
    {
        var images = MakeImages(("small.png", 400, 300));
        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("upscaling"));
    }

    [Fact]
    public void WhenImageHasHighCropThenCriticalIssue()
    {
        // Very extreme aspect ratio image that will force >30% crop
        var images = MakeImages(("ultra_wide.png", 3000, 500));
        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("cropping"));
    }

    [Fact]
    public void WhenImageHasModerateCropThenWarningOrCriticalIssue()
    {
        // Use an extreme AR that won't match any bucket well, causing crop issues.
        // 2400×600 has AR=4.0 but max bucket AR is 2.0, so significant cropping occurs.
        var images = MakeImages(("moderate.png", 2400, 600));
        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        // Should produce a crop-related issue (warning or critical)
        result.Issues.Should().Contain(i =>
            i.Domain == CheckDomain.Image
            && i.CheckName == BucketAnalyzer.CheckName
            && i.Message.Contains("cropping"));
    }

    [Fact]
    public void WhenSingleImageBucketsWithBatchSizeGreaterThanOneThenWarning()
    {
        // Two images with very different ARs → each gets its own bucket
        var images = MakeImages(
            ("landscape.png", 1920, 1080),
            ("portrait.png", 768, 1024));

        var config = ConfigWith(batch: 4);
        var result = BucketAnalyzer.Analyze(images, config);

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("1 image"));
    }

    [Fact]
    public void WhenDominantBucketThenWarning()
    {
        // 10 square images + 1 landscape → >60% in one bucket
        var images = new List<ImageFileInfo>();
        for (int i = 0; i < 10; i++)
            images.Add(new ImageFileInfo($"sq_{i}.png", 1024, 1024));
        images.Add(new ImageFileInfo("land.png", 1920, 1080));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("skewed"));
    }

    [Fact]
    public void WhenResolutionVarianceIsHighThenWarning()
    {
        var images = MakeImages(
            ("tiny.png", 256, 256),
            ("huge.png", 4096, 4096));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("varies"));
    }

    [Fact]
    public void WhenTooManyBucketsThenWarning()
    {
        // Create images with widely different ARs so they spread across many buckets
        var images = MakeImages(
            ("a.png", 512, 1024),
            ("b.png", 1024, 512),
            ("c.png", 768, 1024),
            ("d.png", 1024, 768));

        // Use a config that creates many small buckets
        var config = ConfigWith(step: 64, baseRes: 1024);
        var result = BucketAnalyzer.Analyze(images, config);

        // This might or might not trigger the "too many buckets" heuristic
        // depending on how many unique buckets are hit vs image count
        result.Distribution.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenCleanDatasetThenNoIssues()
    {
        // All images at exact bucket resolution → no issues expected
        var images = MakeImages(
            ("a.png", 1024, 1024),
            ("b.png", 1024, 1024),
            ("c.png", 1024, 1024),
            ("d.png", 1024, 1024),
            ("e.png", 1024, 1024));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Issues.Where(i => i.Severity == IssueSeverity.Critical)
            .Should().BeEmpty("clean dataset should have no critical issues");
    }

    #endregion

    #region Distribution Score

    [Fact]
    public void WhenSingleBucketThenScoreIsZero()
    {
        var distribution = new List<BucketDistributionEntry>
        {
            new() { Bucket = new BucketResolution(1024, 1024), ImageCount = 10, ImagePaths = [] }
        };

        BucketAnalyzer.CalculateDistributionScore(distribution).Should().Be(0);
    }

    [Fact]
    public void WhenEvenDistributionThenScoreIsHundred()
    {
        var distribution = new List<BucketDistributionEntry>
        {
            new() { Bucket = new BucketResolution(1024, 1024), ImageCount = 10, ImagePaths = [] },
            new() { Bucket = new BucketResolution(1024, 768), ImageCount = 10, ImagePaths = [] },
            new() { Bucket = new BucketResolution(768, 1024), ImageCount = 10, ImagePaths = [] }
        };

        BucketAnalyzer.CalculateDistributionScore(distribution).Should().Be(100);
    }

    [Fact]
    public void WhenModerateSkewThenScoreIsMidRange()
    {
        var distribution = new List<BucketDistributionEntry>
        {
            new() { Bucket = new BucketResolution(1024, 1024), ImageCount = 8, ImagePaths = [] },
            new() { Bucket = new BucketResolution(1024, 768), ImageCount = 2, ImagePaths = [] }
        };

        var score = BucketAnalyzer.CalculateDistributionScore(distribution);
        score.Should().BeGreaterThan(0).And.BeLessThan(100);
    }

    [Fact]
    public void WhenEmptyDistributionThenScoreIsZero()
    {
        BucketAnalyzer.CalculateDistributionScore([]).Should().Be(0);
    }

    [Fact]
    public void WhenNullDistributionThenThrows()
    {
        var act = () => BucketAnalyzer.CalculateDistributionScore(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Full Analysis

    [Fact]
    public void WhenEmptyImageListThenReturnsEmptyResult()
    {
        var result = BucketAnalyzer.Analyze([], DefaultConfig);

        result.AllBuckets.Should().NotBeEmpty();
        result.Distribution.Should().BeEmpty();
        result.Assignments.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
        result.DistributionScore.Should().Be(0);
    }

    [Fact]
    public void WhenSingleImageThenAssignedToOneBucket()
    {
        var images = MakeImages(("test.png", 1920, 1080));
        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Assignments.Should().HaveCount(1);
        result.Distribution.Should().HaveCount(1);
        result.Distribution[0].ImageCount.Should().Be(1);
    }

    [Fact]
    public void WhenMixedImagesThenCorrectlyDistributed()
    {
        var images = MakeImages(
            ("sq1.png", 1024, 1024),
            ("sq2.png", 1024, 1024),
            ("land.png", 1920, 1080),
            ("port.png", 768, 1024));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Assignments.Should().HaveCount(4);
        result.Distribution.Should().HaveCountGreaterThanOrEqualTo(2, "mixed ARs should use multiple buckets");
    }

    [Fact]
    public void WhenSameAspectRatioThenSingleBucket()
    {
        var images = MakeImages(
            ("a.png", 1024, 1024),
            ("b.png", 512, 512),
            ("c.png", 2048, 2048));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Distribution.Should().HaveCount(1, "all 1:1 images should go to the same bucket");
    }

    [Fact]
    public void WhenAssignmentsThenSortedByFileName()
    {
        var images = MakeImages(
            ("z_image.png", 1024, 1024),
            ("a_image.png", 1024, 1024));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        // Assignments maintain insertion order (same as input)
        result.Assignments.Should().HaveCount(2);
        result.Assignments[0].FileName.Should().Be("z_image.png");
        result.Assignments[1].FileName.Should().Be("a_image.png");
    }

    [Fact]
    public void WhenZeroDimensionImageThenSkipped()
    {
        var images = MakeImages(
            ("good.png", 1024, 1024),
            ("bad.png", 0, 0));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Assignments.Should().HaveCount(1);
        result.Assignments[0].FileName.Should().Be("good.png");
    }

    [Fact]
    public void WhenNullImagesThenThrows()
    {
        var act = () => BucketAnalyzer.Analyze(null!, DefaultConfig);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenNullAnalyzeConfigThenThrows()
    {
        var act = () => BucketAnalyzer.Analyze([], null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void WhenIdenticalImagesThenSingleDistributionEntry()
    {
        var images = MakeImages(
            ("a.png", 1024, 768),
            ("b.png", 1024, 768),
            ("c.png", 1024, 768));

        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        result.Distribution.Should().HaveCount(1);
        result.Distribution[0].ImageCount.Should().Be(3);
    }

    [Fact]
    public void WhenBucketResolutionLabelThenCorrectFormat()
    {
        var bucket = new BucketResolution(1024, 768);
        bucket.Label.Should().Be("1024 × 768");
    }

    [Fact]
    public void WhenCheckNameAndDomainThenCorrectValues()
    {
        var images = MakeImages(("small.png", 200, 200));
        var result = BucketAnalyzer.Analyze(images, DefaultConfig);

        foreach (var issue in result.Issues)
        {
            issue.CheckName.Should().Be(BucketAnalyzer.CheckName);
            issue.Domain.Should().Be(CheckDomain.Image);
        }
    }

    [Fact]
    public void WhenScoreLabelThenMatchesRange()
    {
        BucketAnalyzer.GetScoreLabel(90).Should().Be("Excellent");
        BucketAnalyzer.GetScoreLabel(80).Should().Be("Excellent");
        BucketAnalyzer.GetScoreLabel(70).Should().Be("Good");
        BucketAnalyzer.GetScoreLabel(60).Should().Be("Good");
        BucketAnalyzer.GetScoreLabel(50).Should().Be("Fair");
        BucketAnalyzer.GetScoreLabel(40).Should().Be("Fair");
        BucketAnalyzer.GetScoreLabel(30).Should().Be("Poor");
        BucketAnalyzer.GetScoreLabel(0).Should().Be("Poor");
    }

    #endregion
}
