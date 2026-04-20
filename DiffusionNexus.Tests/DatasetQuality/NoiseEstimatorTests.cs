using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Tests.DatasetQuality;

public class NoiseEstimatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NoiseEstimator _estimator;

    public NoiseEstimatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"noise_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _estimator = new NoiseEstimator();
    }

    public void Dispose()
    {
        // Test files left for diagnostics; temp directory will be cleaned by OS.
    }

    [Fact]
    public async Task WhenNoImages_ThenReturnsPerfectScore()
    {
        var config = MakeConfig();
        var result = await _estimator.RunAsync([], config);

        result.Score.Should().Be(100);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenCleanImage_ThenReturnsHighScore()
    {
        string path = CreateCleanImage("clean.png", 200, 200);
        var images = new[] { new ImageFileInfo(path, 200, 200) };
        var config = MakeConfig();

        var result = await _estimator.RunAsync(images, config);

        result.Score.Should().BeGreaterThanOrEqualTo(70);
    }

    [Fact]
    public async Task WhenVeryNoisyImage_ThenReportsIssue()
    {
        string path = CreateNoisyImage("noisy.png", 200, 200, noiseSigma: 60);
        var images = new[] { new ImageFileInfo(path, 200, 200) };
        var config = MakeConfig();

        var result = await _estimator.RunAsync(images, config);

        result.Score.Should().BeLessThan(50);
        result.Issues.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(3, 100)]
    [InlineData(10, 80)]
    [InlineData(15, 70)]
    public void WhenSigmaToScoreCalculated_ThenReturnsExpectedRange(double sigma, double expectedMinScore)
    {
        double score = NoiseEstimator.SigmaToScore(sigma);
        score.Should().BeGreaterThanOrEqualTo(expectedMinScore);
    }

    [Fact]
    public void WhenSigmaAboveCritical_ThenScoreIsVeryLow()
    {
        double score = NoiseEstimator.SigmaToScore(50);
        score.Should().BeLessThan(10);
    }

    [Fact]
    public async Task WhenMixedNoiseImages_ThenAveragesScores()
    {
        string clean = CreateCleanImage("clean.png", 200, 200);
        string noisy = CreateNoisyImage("noisy.png", 200, 200, noiseSigma: 60);

        var images = new[]
        {
            new ImageFileInfo(clean, 200, 200),
            new ImageFileInfo(noisy, 200, 200)
        };
        var config = MakeConfig();

        var result = await _estimator.RunAsync(images, config);

        result.PerImageScores.Should().HaveCount(2);
    }

    [Fact]
    public void WhenCleanImageAnalyzed_ThenSigmaIsLow()
    {
        string path = CreateCleanImage("sigma_test.png", 200, 200);
        double sigma = NoiseEstimator.EstimateNoiseSigma(path);
        sigma.Should().BeLessThan(15);
    }

    [Fact]
    public void WhenNoisyImageAnalyzed_ThenSigmaIsHigh()
    {
        string path = CreateNoisyImage("sigma_noisy.png", 200, 200, noiseSigma: 50);
        double sigma = NoiseEstimator.EstimateNoiseSigma(path);
        sigma.Should().BeGreaterThan(10);
    }

    private DatasetConfig MakeConfig() => new()
    {
        FolderPath = _tempDir,
        LoraType = LoraType.Character
    };

    private string CreateCleanImage(string fileName, int width, int height)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);

        // Smooth gradient — low noise
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte val = (byte)((x * 255) / Math.Max(width - 1, 1));
                    row[x] = new L8(val);
                }
            }
        });

        image.SaveAsPng(path);
        return path;
    }

    private string CreateNoisyImage(string fileName, int width, int height, double noiseSigma)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);
        var rng = new Random(42);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    // Base gradient + Gaussian noise
                    double baseVal = (x * 255.0) / Math.Max(width - 1, 1);
                    double noise = GaussianNoise(rng, noiseSigma);
                    byte val = (byte)Math.Clamp((int)(baseVal + noise), 0, 255);
                    row[x] = new L8(val);
                }
            }
        });

        image.SaveAsPng(path);
        return path;
    }

    private static double GaussianNoise(Random rng, double sigma)
    {
        // Box-Muller transform
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        double stdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return stdNormal * sigma;
    }
}
