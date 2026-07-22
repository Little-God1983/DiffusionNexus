using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Covers <see cref="BlurDetector"/> — the Laplacian-variance sharpness check. One of the two
/// <c>IImageQualityCheck</c> implementations that had no tests (issue #443). Uses real, losslessly
/// encoded PNGs synthesized with ImageSharp (the established pattern from the sibling
/// <c>NoiseEstimatorTests</c>/<c>JpegArtifactDetectorTests</c>); a 1-px checkerboard has an
/// exactly-computable Laplacian variance so the score bands are pinned deterministically.
/// </summary>
public class BlurDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BlurDetector _detector;

    public BlurDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"blur_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _detector = new BlurDetector();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task WhenNoImages_ThenReturnsPerfectScore()
    {
        var result = await _detector.RunAsync([], MakeConfig());

        result.Score.Should().Be(100);
        result.Issues.Should().BeEmpty();
        result.PerImageScores.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenSharpImage_ThenHighScoreAndNoIssues()
    {
        // 1-px black/white checkerboard: every interior Laplacian is ±1020 → variance ≈ 1_040_400,
        // far above the Warning threshold (300) → score ≈ 100, zero issues.
        string path = CreateCheckerboard("sharp.png", 200, 200, 0, 255);
        var images = new[] { new ImageFileInfo(path, 200, 200) };

        var result = await _detector.RunAsync(images, MakeConfig());

        result.Score.Should().BeGreaterThanOrEqualTo(95);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenFlatImage_ThenCriticalIssueAndLowScore()
    {
        // A uniform image has zero edge energy → Laplacian variance 0 (< CriticalThreshold 100).
        string path = CreateUniform("flat.png", 200, 200, 128);
        var images = new[] { new ImageFileInfo(path, 200, 200) };

        var result = await _detector.RunAsync(images, MakeConfig());

        result.Score.Should().BeLessThan(10);
        result.Issues.Should().ContainSingle(i => i.Severity == IssueSeverity.Critical);
        result.Issues.Single().AffectedFiles.Should().ContainSingle().Which.Should().Be(path);
    }

    [Fact]
    public async Task WhenSoftImage_ThenWarningIssueButNotCritical()
    {
        // d=3 checkerboard → every interior Laplacian is ±12 → variance exactly 144,
        // which lands in the Warning band (CriticalThreshold 100 ≤ 144 < WarningThreshold 300).
        string path = CreateCheckerboard("soft.png", 200, 200, 126, 129);

        // Guard the synthesis: if the variance drifts out of band the misclassification
        // would silently pass, so assert the pinned value up front.
        BlurDetector.ComputeLaplacianVariance(path).Should().BeApproximately(144.0, 0.5);

        var images = new[] { new ImageFileInfo(path, 200, 200) };
        var result = await _detector.RunAsync(images, MakeConfig());

        result.Issues.Should().ContainSingle(i => i.Severity == IssueSeverity.Warning);
        result.Issues.Should().NotContain(i => i.Severity == IssueSeverity.Critical);
    }

    [Fact]
    public async Task WhenMixedImages_ThenPerImageScoresAveraged()
    {
        string sharp = CreateCheckerboard("mix_sharp.png", 200, 200, 0, 255);
        string flat = CreateUniform("mix_flat.png", 200, 200, 128);
        var images = new[]
        {
            new ImageFileInfo(sharp, 200, 200),
            new ImageFileInfo(flat, 200, 200)
        };

        var result = await _detector.RunAsync(images, MakeConfig());

        result.PerImageScores.Should().HaveCount(2);
        result.Score.Should().BeApproximately(
            Math.Round(result.PerImageScores.Average(s => s.Score), 1), 0.05);
    }

    [Fact]
    public async Task WhenCancelled_ThenThrowsOperationCanceled()
    {
        string path = CreateUniform("cancel.png", 50, 50, 100);
        var images = new[] { new ImageFileInfo(path, 50, 50) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _detector.RunAsync(images, MakeConfig(), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── ComputeLaplacianVariance (internal) ──

    [Fact]
    public void ComputeLaplacianVariance_UniformImage_IsZero()
    {
        string path = CreateUniform("uniform.png", 64, 64, 200);
        BlurDetector.ComputeLaplacianVariance(path).Should().Be(0);
    }

    [Fact]
    public void ComputeLaplacianVariance_HighContrastCheckerboard_IsLarge()
    {
        string path = CreateCheckerboard("hc.png", 64, 64, 0, 255);
        BlurDetector.ComputeLaplacianVariance(path).Should().BeGreaterThan(300);
    }

    [Fact]
    public void ComputeLaplacianVariance_ImageSmallerThan3px_ReturnsZero()
    {
        string path = CreateUniform("tiny.png", 2, 2, 255);
        BlurDetector.ComputeLaplacianVariance(path).Should().Be(0);
    }

    // ── VarianceToScore (internal) — sigmoid mapping ──

    [Theory]
    [InlineData(0.0, 1.8)]      // 100/(1+e^4)
    [InlineData(100.0, 11.9)]   // 100/(1+e^2)
    [InlineData(200.0, 50.0)]   // sigmoid centre
    [InlineData(300.0, 88.1)]   // 100/(1+e^-2)
    public void VarianceToScore_MatchesSigmoid(double variance, double expected)
    {
        BlurDetector.VarianceToScore(variance).Should().BeApproximately(expected, 0.2);
    }

    [Fact]
    public void VarianceToScore_IsMonotonicallyIncreasing()
    {
        double low = BlurDetector.VarianceToScore(50);
        double mid = BlurDetector.VarianceToScore(200);
        double high = BlurDetector.VarianceToScore(1000);
        low.Should().BeLessThan(mid);
        mid.Should().BeLessThan(high);
        high.Should().BeLessThan(100);
    }

    private DatasetConfig MakeConfig() => new()
    {
        FolderPath = _tempDir,
        LoraType = LoraType.Character
    };

    private string CreateUniform(string fileName, int width, int height, byte value)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height, new L8(value));
        image.SaveAsPng(path);
        return path;
    }

    /// <summary>
    /// 1-pixel checkerboard alternating <paramref name="a"/> / <paramref name="b"/> by pixel parity.
    /// For a 1-px cell every orthogonal neighbour is the opposite value, so each interior Laplacian
    /// equals ±4·|a−b| exactly, giving a deterministic variance independent of the platform.
    /// </summary>
    private string CreateCheckerboard(string fileName, int width, int height, byte a, byte b)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                    row[x] = new L8(((x + y) & 1) == 0 ? a : b);
            }
        });
        image.SaveAsPng(path);
        return path;
    }
}
