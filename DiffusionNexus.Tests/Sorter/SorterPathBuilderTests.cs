using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public class SorterPathBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    public void PlaceholderBaseModelsAreDetected(string? raw)
        => SorterPathBuilder.IsPlaceholderBaseModel(raw).Should().BeTrue();

    [Fact]
    public void RealBaseModelIsNotPlaceholder()
        => SorterPathBuilder.IsPlaceholderBaseModel("SDXL 1.0").Should().BeFalse();

    [Fact]
    public void SanitizeReplacesInvalidCharsAndTrimsTrailingDots()
        => SorterPathBuilder.SanitizeFolderName("Pony/XL: v2.").Should().Be("Pony_XL_ v2");

    [Fact]
    public void SanitizeOfOnlyInvalidCharsYieldsUnderscore()
        => SorterPathBuilder.SanitizeFolderName("..").Should().Be("_");

    [Fact]
    public void BaseModelOnlyStructureOmitsCategory()
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", "Character", includeCategory: false)
            .Should().Be(@"E:\Loras\SDXL 1.0");

    [Fact]
    public void CategoryStructureAppendsCategoryFolder()
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", "Character", includeCategory: true)
            .Should().Be(@"E:\Loras\SDXL 1.0\Character");

    [Fact]
    public void PlaceholderBaseModelMapsToUnknownFolder()
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", "???", "Style", includeCategory: true)
            .Should().Be(@"E:\Loras\Unknown\Style");

    [Fact]
    public void FreeNameIsKeptPlain()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", 3204603, _ => false)
            .Should().Be("V1.safetensors");

    [Fact]
    public void TakenNameGetsVersionIdSuffix()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", 3204603,
                n => n == "V1.safetensors")
            .Should().Be("V1_3204603.safetensors");

    [Fact]
    public void WithoutVersionIdNumericSuffixIsUsed()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", null,
                n => n == "V1.safetensors")
            .Should().Be("V1_2.safetensors");

    [Fact]
    public void NumericSuffixSkipsTakenCandidates()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", null,
                n => n is "V1.safetensors" or "V1_2.safetensors")
            .Should().Be("V1_3.safetensors");

    [Fact]
    public void TakenVersionIdSuffixFallsBackToNumeric()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", 42,
                n => n is "V1.safetensors" or "V1_42.safetensors")
            .Should().Be("V1_2.safetensors");
}
