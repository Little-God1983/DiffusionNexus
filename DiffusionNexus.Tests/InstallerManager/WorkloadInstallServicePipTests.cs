using DiffusionNexus.Installer.SDK.Models.Configuration;
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
    public void WhenRequirementsContainTransformersThenReturnsEmpty()
    {
        // The transformers/kernels/huggingface_hub triangle is now handled by the
        // post-loop trio upgrade in RepairPipDependenciesAsync, not by the per-node
        // supplementary resolver. The dictionary is empty for now.
        var requirementsPath = CreateTempRequirements(
            "torch",
            "transformers>=4.57.1",
            "numpy");

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

            result.Should().BeEmpty();
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

    #region RequirementsContainsTrioTrigger

    [Theory]
    [InlineData("transformers>=4.57.1")]
    [InlineData("transformers")]
    [InlineData("transformers[torch]==5.0")]
    [InlineData("kernels")]
    [InlineData("kernels>=0.1")]
    public void WhenRequirementsContainTransformersOrKernelsThenTriggersTrioUpgrade(
        string line)
    {
        var requirementsPath = CreateTempRequirements("torch", line, "numpy");

        try
        {
            var result = WorkloadInstallService.RequirementsContainsTrioTrigger(requirementsPath);

            result.Should().BeTrue();
        }
        finally
        {
            File.Delete(requirementsPath);
        }
    }

    [Fact]
    public void WhenRequirementsHaveNoTrioPackagesThenDoesNotTrigger()
    {
        var requirementsPath = CreateTempRequirements("torch", "numpy", "pillow");

        try
        {
            var result = WorkloadInstallService.RequirementsContainsTrioTrigger(requirementsPath);

            result.Should().BeFalse();
        }
        finally
        {
            File.Delete(requirementsPath);
        }
    }

    [Fact]
    public void WhenTransformersOnlyAppearsInACommentThenDoesNotTrigger()
    {
        var requirementsPath = CreateTempRequirements(
            "torch",
            "# transformers >= 4.57.1 was removed",
            "numpy");

        try
        {
            var result = WorkloadInstallService.RequirementsContainsTrioTrigger(requirementsPath);

            result.Should().BeFalse();
        }
        finally
        {
            File.Delete(requirementsPath);
        }
    }

    [Fact]
    public void WhenRequirementsFileMissingThenDoesNotTrigger()
    {
        var result = WorkloadInstallService.RequirementsContainsTrioTrigger(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "requirements.txt"));

        result.Should().BeFalse();
    }

    #endregion

    private static string CreateTempRequirements(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_requirements_{Guid.NewGuid()}.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Theory]
    [InlineData("https://github.com/JamePeng/llama-cpp-python/releases/download/v0.4.2-cu130-win-20261003/llama_cpp_python-0.4.2+cu130-cp312-cp312-win_amd64.whl", "0.4.2+cu130")]
    [InlineData("https://example/llama_cpp_python-0.3.20-cp312-cp312-win_amd64.whl", "0.3.20")]
    [InlineData(@"E:\wheels\llama_cpp_python-0.3.20-cp312-cp312-win_amd64.whl", "0.3.20")]
    [InlineData("https://example/not-a-wheel.zip", null)]
    [InlineData("", null)]
    public void WheelVersionFromUrl_ReadsTheVersionSegment(string url, string? expected)
    {
        WorkloadInstallService.WheelVersionFromUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("DN_VERSION=0.4.2+cu130\n", "0.4.2+cu130")]
    [InlineData("[llama-cpp-python] loaded bundled OpenMP runtime: C:\\x\\libomp.dll\r\n[llama-cpp-python].find_library: loaded ggml.dll\r\nDN_VERSION=0.4.2+cu130\r\n", "0.4.2+cu130")]
    [InlineData("0.4.2+cu130\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseMarkedVersion_IgnoresTheModulesOwnOutput(string? stdout, string? expected)
    {
        WorkloadInstallService.ParseMarkedVersion(stdout).Should().Be(expected);
    }

    // Review (#607): the wheel came from a third-party release with no integrity check although the catalog has its hash.
    [Theory]
    [InlineData("sha256:693CEF0Babc", "https://x/llama_cpp_python-0.4.2+cu130-cp312-cp312-win_amd64.whl#sha256=693cef0babc")]
    [InlineData("693cef0babc", "https://x/llama_cpp_python-0.4.2+cu130-cp312-cp312-win_amd64.whl#sha256=693cef0babc")]
    [InlineData("", "https://x/llama_cpp_python-0.4.2+cu130-cp312-cp312-win_amd64.whl")]
    public void PipWheelRequirement_CarriesTheCatalogHash_SoPipRejectsAnyOtherFile(string sha256, string expected)
    {
        var wheel = new LamaCppWheel { Url = "https://x/llama_cpp_python-0.4.2+cu130-cp312-cp312-win_amd64.whl", Sha256 = sha256 };

        WorkloadInstallService.PipWheelRequirement(wheel).Should().Be(expected);
    }

    private static readonly LamaCppWheel Cu128 = new() { IsGPU = true, PythonVersion = "3.12", CudaVersion = "12.8", Url = "https://x/a.whl" };
    private static readonly LamaCppWheel Cu130 = new() { IsGPU = true, PythonVersion = "3.12", CudaVersion = "13.0", Url = "https://x/b.whl" };

    [Fact]
    public void PickLlamaCppWheel_MatchesCudaAndPython_OrNothing()
    {
        WorkloadInstallService.PickLlamaCppWheel([Cu128, Cu130], "13.0", "3.12").Should().BeSameAs(Cu130);
        WorkloadInstallService.PickLlamaCppWheel([Cu128, Cu130], "12.8", "3.12").Should().BeSameAs(Cu128);
        WorkloadInstallService.PickLlamaCppWheel([Cu128, Cu130], "12.4", "3.12").Should().BeNull("another CUDA's wheel loads but runs on the CPU");
        WorkloadInstallService.PickLlamaCppWheel([Cu130], "13.0", "3.13").Should().BeNull("a cp312 wheel does not install into Python 3.13");
    }

    [Theory]
    [InlineData("DN_PYTHON=3.12\r\nDN_CUDA=13.0\r\n", "3.12", "13.0")]
    [InlineData("DN_PYTHON=3.13\n", "3.13", null)]          // torch missing: the import failed after the Python line
    [InlineData("DN_PYTHON=3.12\nDN_CUDA=None\n", "3.12", null)] // a CPU torch
    [InlineData(null, null, null)]
    public void ParsePythonAndCuda_ReadsTheVenvProbe(string? stdout, string? python, string? cuda)
    {
        WorkloadInstallService.ParsePythonAndCuda(stdout).Should().Be((python, cuda));
    }

    [Theory]
    [InlineData("Collecting x\nERROR: THESE PACKAGES DO NOT MATCH THE HASHES FROM THE REQUIREMENTS FILE.\n    llama_cpp_python ...\n", "ERROR: THESE PACKAGES DO NOT MATCH THE HASHES FROM THE REQUIREMENTS FILE.")]
    [InlineData("WARNING: x\nsomething broke\n\n", "something broke")]
    [InlineData("", "see the log")]
    public void PipFailureReason_PrefersPipsErrorLine(string stderr, string expected)
    {
        WorkloadInstallService.PipFailureReason(stderr).Should().Be(expected);
    }
}
