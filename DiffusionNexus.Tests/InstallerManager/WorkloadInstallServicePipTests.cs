using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Tests for <see cref="WorkloadInstallService.ResolveSupplementaryPackages"/>
/// and <see cref="WorkloadInstallService.ExtractPackageName"/>. Verifies that
/// supplementary pip packages (e.g. <c>kernels</c> for <c>transformers</c>) are
/// correctly resolved from requirements.txt content.
/// </summary>
public class WorkloadInstallServicePipTests
{
    #region ExtractPackageName

    [Theory]
    [InlineData("transformers>=4.57.1", "transformers")]
    [InlineData("transformers==4.57.1", "transformers")]
    [InlineData("transformers~=4.57", "transformers")]
    [InlineData("transformers!=4.50", "transformers")]
    [InlineData("transformers<5.0", "transformers")]
    [InlineData("transformers>4.57", "transformers")]
    [InlineData("transformers<=5.0", "transformers")]
    [InlineData("torch", "torch")]
    [InlineData("numpy ", "numpy")]
    [InlineData("  pillow  ", "pillow")]
    public void WhenLineHasVersionSpecifierThenExtractsPackageName(
        string line, string expected)
    {
        var result = WorkloadInstallService.ExtractPackageName(line);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("triton-windows; sys_platform == 'win32'", "triton-windows")]
    [InlineData("triton; sys_platform == 'linux'", "triton")]
    public void WhenLineHasEnvironmentMarkerThenStripsMarker(
        string line, string expected)
    {
        var result = WorkloadInstallService.ExtractPackageName(line);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("qwen-vl-utils[decord]", "qwen-vl-utils")]
    [InlineData("package[extra1,extra2]>=1.0", "package")]
    public void WhenLineHasExtrasThenStripsExtras(
        string line, string expected)
    {
        var result = WorkloadInstallService.ExtractPackageName(line);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("torch_audio", "torch-audio")]
    [InlineData("huggingface_hub", "huggingface-hub")]
    public void WhenLineHasUnderscoresThenNormalisesToHyphens(
        string line, string expected)
    {
        var result = WorkloadInstallService.ExtractPackageName(line);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("# this is a comment")]
    [InlineData("-r other_requirements.txt")]
    [InlineData("-e git+https://github.com/...")]
    [InlineData("--find-links https://...")]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenLineIsCommentOrFlagThenReturnsEmpty(string line)
    {
        var result = WorkloadInstallService.ExtractPackageName(line);

        result.Should().BeEmpty();
    }

    [Fact]
    public void WhenLineHasInlineCommentThenStripsComment()
    {
        var result = WorkloadInstallService.ExtractPackageName("numpy>=1.20 # for array support");

        result.Should().Be("numpy");
    }

    #endregion

    #region ResolveSupplementaryPackages

    [Fact]
    public void WhenRequirementsContainTransformersThenReturnsKernels()
    {
        var requirementsPath = CreateTempRequirements(
            "torch",
            "transformers>=4.57.1",
            "numpy");

        try
        {
            var result = WorkloadInstallService.ResolveSupplementaryPackages(requirementsPath);

            result.Should().ContainSingle()
                .Which.Should().Be("kernels");
        }
        finally
        {
            File.Delete(requirementsPath);
        }
    }

    [Fact]
    public void WhenRequirementsDoNotContainKnownPackagesThenReturnsEmpty()
    {
        var requirementsPath = CreateTempRequirements(
            "torch",
            "numpy",
            "pillow");

        try
        {
            var result = WorkloadInstallService.ResolveSupplementaryPackages(requirementsPath);

            result.Should().BeEmpty();
        }
        finally
        {
            File.Delete(requirementsPath);
        }
    }

    [Fact]
    public void WhenRequirementsContainCommentsAndBlankLinesThenIgnoresThem()
    {
        var requirementsPath = CreateTempRequirements(
            "# Main dependencies",
            "",
            "torch",
            "# transformers is needed for inference",
            "transformers>=4.57.1",
            "");

        try
        {
            var result = WorkloadInstallService.ResolveSupplementaryPackages(requirementsPath);

            result.Should().ContainSingle()
                .Which.Should().Be("kernels");
        }
        finally
        {
            File.Delete(requirementsPath);
        }
    }

    [Fact]
    public void WhenTransformersAppearsMultipleTimesThenDeduplicatesSupplementary()
    {
        var requirementsPath = CreateTempRequirements(
            "transformers>=4.57.1",
            "transformers[torch]>=4.57.1");

        try
        {
            var result = WorkloadInstallService.ResolveSupplementaryPackages(requirementsPath);

            result.Should().ContainSingle()
                .Which.Should().Be("kernels");
        }
        finally
        {
            File.Delete(requirementsPath);
        }
    }

    [Fact]
    public void WhenFileDoesNotExistThenReturnsEmpty()
    {
        var result = WorkloadInstallService.ResolveSupplementaryPackages(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "requirements.txt"));

        result.Should().BeEmpty();
    }

    #endregion

    private static string CreateTempRequirements(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_requirements_{Guid.NewGuid()}.txt");
        File.WriteAllLines(path, lines);
        return path;
    }
}
