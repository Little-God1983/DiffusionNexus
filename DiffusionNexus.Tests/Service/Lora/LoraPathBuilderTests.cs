using DiffusionNexus.Service.Services.Lora;
using FluentAssertions;

namespace DiffusionNexus.Tests.Service.Lora;

public class LoraPathBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    public void PlaceholderBaseModelsAreDetected(string? raw)
        => LoraPathBuilder.IsPlaceholderBaseModel(raw).Should().BeTrue();

    [Fact]
    public void RealBaseModelIsNotPlaceholder()
        => LoraPathBuilder.IsPlaceholderBaseModel("SDXL 1.0").Should().BeFalse();

    [Fact]
    public void SanitizeReplacesInvalidCharsAndTrimsTrailingDots()
        => LoraPathBuilder.SanitizeFolderName("Pony/XL: v2.").Should().Be("Pony_XL_ v2");

    [Fact]
    public void SanitizeOfOnlyInvalidCharsYieldsUnderscore()
        => LoraPathBuilder.SanitizeFolderName("..").Should().Be("_");

    [Fact]
    public void BaseModelOnlyStructureOmitsCategory()
        => LoraPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", "Character", includeCategory: false)
            .Should().Be(@"E:\Loras\SDXL 1.0");

    [Fact]
    public void CategoryStructureAppendsCategoryFolder()
        => LoraPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", "Character", includeCategory: true)
            .Should().Be(@"E:\Loras\SDXL 1.0\Character");

    [Fact]
    public void PlaceholderBaseModelMapsToUnknownFolder()
        => LoraPathBuilder.BuildTargetDirectory(@"E:\Loras", "???", "Style", includeCategory: true)
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
        => LoraPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", category, includeCategory: true)
            .Should().Be(@"E:\Loras\SDXL 1.0");

    [Fact]
    public void UnresolvedCategoryStillKeepsTheUnknownBaseModelFolder()
        => LoraPathBuilder.BuildTargetDirectory(@"E:\Loras", null, "Unknown", includeCategory: true)
            .Should().Be(@"E:\Loras\Unknown");

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("Unknown", true)]
    [InlineData("Character", false)]
    public void IsUnresolvedCategoryDetectsTheNoSegmentCases(string? category, bool expected)
        => LoraPathBuilder.IsUnresolvedCategory(category).Should().Be(expected);

    [Fact]
    public void CandidateNamesStartPlainThenTakeTheVersionIdThenNumbers()
        => LoraPathBuilder.EnumerateCandidateNames("V1.safetensors", 3204603).Take(4)
            .Should().Equal("V1.safetensors", "V1_3204603.safetensors", "V1_2.safetensors", "V1_3.safetensors");

    [Fact]
    public void WithoutAVersionIdTheSequenceIsPlainThenNumbers()
        => LoraPathBuilder.EnumerateCandidateNames("V1.safetensors", null).Take(4)
            .Should().Equal("V1.safetensors", "V1_2.safetensors", "V1_3.safetensors", "V1_4.safetensors");

    [Fact]
    public void CandidateNamesKeepTheExtensionAndTheFullStem()
        => LoraPathBuilder.EnumerateCandidateNames("my.lora.v2.ckpt", null).Take(2)
            .Should().Equal("my.lora.v2.ckpt", "my.lora.v2_2.ckpt");

    [Fact]
    public void BuildTargetDirectory_WithoutBaseModelSegment_SkipsUnknownToo()
        => LoraPathBuilder.BuildTargetDirectory(@"C:\root", null, "Style", includeBaseModel: false, includeCategory: true)
            .Should().Be(@"C:\root\Style");

    [Fact]
    public void BuildTargetDirectory_DownloadShape_SanitizesTheSegments()
        => LoraPathBuilder.BuildTargetDirectory(@"C:\root", "SD 3.5?", "Chara<cter", includeBaseModel: true, includeCategory: true)
            .Should().Be(@"C:\root\SD 3.5_\Chara_cter");

    /// <summary>
    /// HuggingFace's save_pretrained shard convention, and only it. The suffix is anchored at the
    /// end of the stem because the rule is "this file is a fragment of a set", not "this name has
    /// digits in it" — a LoRA carrying the same shape mid-name owns its destination as usual.
    /// </summary>
    [Theory]
    [InlineData(@"C:\m\model-00001-of-00004.safetensors", true)]
    [InlineData(@"C:\m\model-00004-of-00004.safetensors", true)]
    [InlineData(@"C:\m\diffusion_pytorch_model-00002-of-00003.safetensors", true)]
    [InlineData(@"C:\m\model-00001-of-00004-finetune.safetensors", false)]
    [InlineData(@"C:\m\model-1-of-4.safetensors", false)]
    [InlineData(@"C:\m\MyCharacterLora.safetensors", false)]
    [InlineData(@"C:\m\clip_l.safetensors", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void AShardOfASplitModelIsRecognizedByItsAnchoredSuffix(string? path, bool expected)
        => LoraPathBuilder.IsShardOfASplitModel(path).Should().Be(expected);
}
