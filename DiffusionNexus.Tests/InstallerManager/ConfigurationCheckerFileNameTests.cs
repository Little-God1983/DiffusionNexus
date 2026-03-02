using DiffusionNexus.UI.Services.ConfigurationChecker;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Tests for the configured file name derivation and URL filename extraction
/// in <see cref="ConfigurationCheckerService"/>. Covers the bug where HuggingFace
/// models with generic file names (e.g. diffusion_pytorch_model.safetensors) were
/// not found when the configured model name differed from the URL filename.
/// </summary>
public class ConfigurationCheckerFileNameTests
{
    [Theory]
    [InlineData(
        "Qwen-Image-InstantX-ControlNet-Inpainting",
        "diffusion_pytorch_model.safetensors",
        "Qwen-Image-InstantX-ControlNet-Inpainting.safetensors")]
    [InlineData(
        "my-model",
        "model.gguf",
        "my-model.gguf")]
    [InlineData(
        "already-named.safetensors",
        "diffusion_pytorch_model.safetensors",
        "already-named.safetensors")]
    public void WhenNamesDifferThenDeriveConfiguredFileNameReturnsConfiguredName(
        string modelName, string urlFileName, string expected)
    {
        var result = ConfigurationCheckerService.DeriveConfiguredFileName(modelName, urlFileName);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("diffusion_pytorch_model", "diffusion_pytorch_model.safetensors")]
    [InlineData("same-file.safetensors", "same-file.safetensors")]
    public void WhenNamesMatchThenDeriveConfiguredFileNameReturnsNull(
        string modelName, string urlFileName)
    {
        var result = ConfigurationCheckerService.DeriveConfiguredFileName(modelName, urlFileName);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("", "file.safetensors")]
    [InlineData("model", "")]
    [InlineData(null, "file.safetensors")]
    [InlineData("model", null)]
    public void WhenInputIsNullOrEmptyThenDeriveConfiguredFileNameReturnsNull(
        string? modelName, string? urlFileName)
    {
        var result = ConfigurationCheckerService.DeriveConfiguredFileName(modelName!, urlFileName!);

        result.Should().BeNull();
    }

    [Fact]
    public void WhenUrlFileHasNoExtensionThenDeriveConfiguredFileNameReturnsNull()
    {
        var result = ConfigurationCheckerService.DeriveConfiguredFileName("my-model", "noextension");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(
        "https://huggingface.co/org/repo/resolve/main/diffusion_pytorch_model.safetensors",
        "diffusion_pytorch_model.safetensors")]
    [InlineData(
        "https://huggingface.co/org/repo/resolve/main/qwen-image-2512-Q8_0.gguf",
        "qwen-image-2512-Q8_0.gguf")]
    [InlineData(
        "https://huggingface.co/org/repo/resolve/main/Qwen-Image-Lightning-4steps-V1.0.safetensors",
        "Qwen-Image-Lightning-4steps-V1.0.safetensors")]
    public void WhenUrlIsValidThenGetFileNameFromUrlExtractsCorrectFileName(
        string url, string expected)
    {
        var result = ConfigurationCheckerService.GetFileNameFromUrl(url);

        result.Should().Be(expected);
    }
}
