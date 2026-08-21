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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    public void UnresolvedCategoryOmitsTheSegmentLikeTheDownloader(string? category)
        // Review 4.1: DownloadDestinationViewModel.BuildTargetDirectory skips the category
        // segment when it is empty, so a downloaded but uncategorized LoRA lives at
        // {root}\{BaseModel}\. The sorter appended "Unknown\" — every sort run dragged
        // those files down a level and the next download re-created them one level up.
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", category, includeCategory: true)
            .Should().Be(@"E:\Loras\SDXL 1.0");

    [Fact]
    public void UnresolvedCategoryStillKeepsTheUnknownBaseModelFolder()
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", null, "Unknown", includeCategory: true)
            .Should().Be(@"E:\Loras\Unknown");

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("Unknown", true)]
    [InlineData("Character", false)]
    public void IsUnresolvedCategoryDetectsTheNoSegmentCases(string? category, bool expected)
        => SorterPathBuilder.IsUnresolvedCategory(category).Should().Be(expected);

    [Fact]
    public void CandidateNamesStartPlainThenTakeTheVersionIdThenNumbers()
        => SorterPathBuilder.EnumerateCandidateNames("V1.safetensors", 3204603).Take(4)
            .Should().Equal("V1.safetensors", "V1_3204603.safetensors", "V1_2.safetensors", "V1_3.safetensors");

    [Fact]
    public void WithoutAVersionIdTheSequenceIsPlainThenNumbers()
        => SorterPathBuilder.EnumerateCandidateNames("V1.safetensors", null).Take(4)
            .Should().Equal("V1.safetensors", "V1_2.safetensors", "V1_3.safetensors", "V1_4.safetensors");

    [Fact]
    public void CandidateNamesKeepTheExtensionAndTheFullStem()
        => SorterPathBuilder.EnumerateCandidateNames("my.lora.v2.ckpt", null).Take(2)
            .Should().Equal("my.lora.v2.ckpt", "my.lora.v2_2.ckpt");
}
