using DiffusionNexus.Domain.Enums;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain;

/// <summary>
/// The support-asset set and its names are stated once, here, because three surfaces read them —
/// the sorter's destination folder, the chip on that folder's row, and the Viewer's badge — and a
/// folder whose name disagreed with the chip above it would be a bug nobody could see in a diff.
/// </summary>
public sealed class ModelTypeExtensionsTests
{
    [Theory]
    [InlineData(ModelType.VAE)]
    [InlineData(ModelType.Controlnet)]
    [InlineData(ModelType.Upscaler)]
    [InlineData(ModelType.TextEncoder)]
    public void SupportAssetsAreNotLoras(ModelType type)
        => type.IsSupportAsset().Should().BeTrue();

    [Theory]
    [InlineData(ModelType.LORA)]
    [InlineData(ModelType.Checkpoint)]
    [InlineData(ModelType.LoCon)]
    [InlineData(ModelType.DoRA)]
    [InlineData(ModelType.Unknown)]
    public void EverythingTheSorterIsForIsNotASupportAsset(ModelType type)
        => type.IsSupportAsset().Should().BeFalse();

    [Theory]
    [InlineData(ModelType.VAE, "VAE")]
    [InlineData(ModelType.Controlnet, "ControlNet")]
    [InlineData(ModelType.Upscaler, "Upscaler")]
    [InlineData(ModelType.TextEncoder, "Text Encoder")]
    [InlineData(ModelType.LORA, "LoRA")]
    public void DisplayNamesAreTheOnesUsersSee(ModelType type, string expected)
        => type.DisplayName().Should().Be(expected);

    /// <summary>
    /// The destination folder and the chip on its row must be the same string, or the preview
    /// would name a folder the sorter does not create.
    /// </summary>
    [Fact]
    public void EverySupportAssetFolderNameIsItsDisplayName()
    {
        foreach (var kind in ModelTypeExtensions.SupportAssetKinds)
            kind.SupportFolderName().Should().Be(kind.DisplayName());
    }

    /// <summary>
    /// A LoRA's folder is its base model, which is a different question — so it deliberately has a
    /// display name but no folder name, and a caller that asks gets null rather than a folder
    /// called "LoRA" appearing beside the base-model folders.
    /// </summary>
    [Fact]
    public void ALoraHasNoSupportFolder()
        => ModelType.LORA.SupportFolderName().Should().BeNull();

    [Fact]
    public void SupportAssetKindsAndIsSupportAssetCannotDisagree()
    {
        foreach (var type in Enum.GetValues<ModelType>())
            type.IsSupportAsset().Should().Be(ModelTypeExtensions.SupportAssetKinds.Contains(type));
    }
}
