using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Tests.DatasetQuality;

public class PerceptualHasherTests : IDisposable
{
    private readonly string _tempDir;

    public PerceptualHasherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"phash_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Test files left for diagnostics; temp directory will be cleaned by OS.
    }

    [Fact]
    public void WhenIdenticalImagesHashed_ThenHammingDistanceIsZero()
    {
        // Arrange
        string imagePath = CreateSolidColorImage("identical.png", 100, 100, new L8(128));
        string copyPath = Path.Combine(_tempDir, "identical_copy.png");
        File.Copy(imagePath, copyPath);

        // Act
        ulong hash1 = PerceptualHasher.ComputeHash(imagePath);
        ulong hash2 = PerceptualHasher.ComputeHash(copyPath);

        // Assert
        PerceptualHasher.HammingDistance(hash1, hash2).Should().Be(0);
    }

    [Fact]
    public void WhenCompletelyDifferentImages_ThenHammingDistanceIsHigh()
    {
        // Arrange
        string whitePath = CreateSolidColorImage("white.png", 100, 100, new L8(255));
        string blackPath = CreateSolidColorImage("black.png", 100, 100, new L8(0));

        // Act
        ulong hashWhite = PerceptualHasher.ComputeHash(whitePath);
        ulong hashBlack = PerceptualHasher.ComputeHash(blackPath);
        int distance = PerceptualHasher.HammingDistance(hashWhite, hashBlack);

        // Assert — solid white vs solid black should differ significantly
        distance.Should().BeGreaterThan(PerceptualHasher.NearDuplicateThreshold);
    }

    [Fact]
    public void WhenImageResized_ThenHashRemainsSimilar()
    {
        // Arrange — create a patterned image and save a resized copy of the same image
        string originalPath = CreateGradientImage("gradient_original.png", 200, 200);

        // Load and resize the same image to create a genuinely resized copy
        string resizedPath = Path.Combine(_tempDir, "gradient_resized.png");
        using (var img = Image.Load<L8>(originalPath))
        {
            img.Mutate(x => x.Resize(150, 150));
            img.SaveAsPng(resizedPath);
        }

        // Act
        ulong hashOriginal = PerceptualHasher.ComputeHash(originalPath);
        ulong hashResized = PerceptualHasher.ComputeHash(resizedPath);
        int distance = PerceptualHasher.HammingDistance(hashOriginal, hashResized);

        // Assert — resized version of same image should be reasonably similar
        // (resampling artifacts may increase distance slightly beyond the near-duplicate threshold)
        distance.Should().BeLessThan(15, "resized version of the same image should remain perceptually similar");
    }

    [Theory]
    [InlineData(0, 100.0)]
    [InlineData(32, 50.0)]
    [InlineData(64, 0.0)]
    public void WhenSimilarityPercentCalculated_ThenReturnsExpectedValue(int hammingDistance, double expectedPercent)
    {
        double result = PerceptualHasher.SimilarityPercent(hammingDistance);
        result.Should().Be(expectedPercent);
    }

    [Fact]
    public void WhenHammingDistanceOfSameHash_ThenReturnsZero()
    {
        ulong hash = 0xDEADBEEFCAFEBABE;
        PerceptualHasher.HammingDistance(hash, hash).Should().Be(0);
    }

    [Fact]
    public void WhenHammingDistanceOfOppositeHashes_ThenReturnsSixtyFour()
    {
        PerceptualHasher.HammingDistance(0UL, ulong.MaxValue).Should().Be(64);
    }

    [Fact]
    public void WhenComputeMedianWithOddCount_ThenReturnsMiddle()
    {
        double[] values = [1.0, 3.0, 2.0, 5.0, 4.0];
        double median = PerceptualHasher.ComputeMedian(values);
        median.Should().Be(3.0);
    }

    [Fact]
    public void WhenComputeMedianWithEvenCount_ThenReturnsAverage()
    {
        double[] values = [1.0, 2.0, 3.0, 4.0];
        double median = PerceptualHasher.ComputeMedian(values);
        median.Should().Be(2.5);
    }

    [Fact]
    public void WhenComputeMedianWithStartIndex_ThenSkipsFirstElements()
    {
        // DC component at index 0 should be skipped
        double[] values = [999.0, 1.0, 3.0, 2.0];
        double median = PerceptualHasher.ComputeMedian(values, startIndex: 1);
        median.Should().Be(2.0);
    }

    [Fact]
    public void WhenDct2DApplied_ThenOutputHasCorrectDimensions()
    {
        int size = 8;
        double[,] input = new double[size, size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                input[y, x] = (y + x) * 10.0;

        double[,] result = PerceptualHasher.ComputeDct2D(input);

        result.GetLength(0).Should().Be(size);
        result.GetLength(1).Should().Be(size);
    }

    private string CreateSolidColorImage(string fileName, int width, int height, L8 color)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                    row[x] = color;
            }
        });
        image.SaveAsPng(path);
        return path;
    }

    private string CreateGradientImage(string fileName, int width, int height)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte val = (byte)((x + y) * 255 / (width + height));
                    row[x] = new L8(val);
                }
            }
        });
        image.SaveAsPng(path);
        return path;
    }
}
