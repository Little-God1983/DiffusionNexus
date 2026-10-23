using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Every name here is a real one, taken from a library where 35 of 328 unidentified files turned
/// out not to be LoRAs at all. The classifier is name-based and so fallible by construction, which
/// is why it only drives a label — but a marker that fires on an ordinary LoRA name would put a
/// wrong chip on a folder, so the negative cases matter as much as the positive ones.
/// </summary>
public sealed class AssetKindClassifierTests
{
    [Theory]
    [InlineData("Wan2_2_VAE_bf16.safetensors")]
    [InlineData("SD3-VAE.safetensors")]
    [InlineData("qwen_image_layered_vae.safetensors")]
    [InlineData("ltx-2.3-22b-dev_audio_vae.safetensors")]
    [InlineData("flux2-vae.safetensors")]
    public void Classify_NamesAVae(string fileName)
        => AssetKindClassifier.Classify(fileName).Should().Be(ModelType.VAE);

    [Theory]
    [InlineData("CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors")]
    [InlineData("clip_g_hidream.safetensors")]
    [InlineData("clip_l.safetensors")]
    [InlineData("umt5-xxl-enc-bf16.safetensors")]
    [InlineData("google_t5-v1_1-xxl_encoderonly-fp8_e4m3fn.safetensors")]
    [InlineData("mistral_3_small_flux2_fp8.safetensors")]
    [InlineData("qwen_2.5_vl_7b_fp8_scaled.safetensors")]
    public void Classify_NamesATextEncoder(string fileName)
        => AssetKindClassifier.Classify(fileName).Should().Be(ModelType.TextEncoder);

    [Theory]
    [InlineData("Qwen-Image-InstantX-ControlNet-Inpainting.safetensors")]
    [InlineData("Z-Image-Turbo-Fun-Controlnet-Union-2.1.safetensors")]
    public void Classify_NamesAControlNet(string fileName)
        => AssetKindClassifier.Classify(fileName).Should().Be(ModelType.Controlnet);

    [Theory]
    [InlineData("4x-UltraSharp.pth")]
    [InlineData("4xLSDIRplus.pth")]
    [InlineData("ltx-2.3-spatial-upscaler-x2-1.1.safetensors")]
    public void Classify_NamesAnUpscaler(string fileName)
        => AssetKindClassifier.Classify(fileName).Should().Be(ModelType.Upscaler);

    /// <summary>
    /// The default has to hold for ordinary LoRA names, including the awkward ones: a bare ".pth"
    /// is not an upscaler (<c>Chris.pth</c> is a real model in that library), "clip" mid-name is not
    /// a text encoder, and an opaque hash name is just a LoRA nobody named well.
    /// </summary>
    [Theory]
    [InlineData("MyChar_Pony_v2.safetensors")]
    [InlineData("Chris.pth")]
    [InlineData("hair_clip_v1.safetensors")]
    [InlineData("BRFHE7KV2VWXY8N3D4SXR4XCT0.safetensors")]
    [InlineData("LTX2.3-MysticXXX.safetensors")]
    [InlineData("Dunking_Basketball_HighNoise.safetensors")]
    // Markers that sat below the bar and had no row proving it. "upscale" and "redux" are gone
    // outright; "vl" survives only next to the family whose encoders spell it that way.
    [InlineData("hires_upscale_helper.safetensors")]
    [InlineData("detail_upscale_v2.safetensors")]
    [InlineData("flux_redux_style_lora.safetensors")]
    [InlineData("anime_vl_style.safetensors")]
    // A chained dimension is not a scale factor: 'x' is a letter, so the remainder test used to
    // accept this one while correctly rejecting "4x4".
    [InlineData("2x2x2_grid_lora.safetensors")]
    [InlineData("4x4_tiles.safetensors")]
    public void Classify_DefaultsToLora(string fileName)
        => AssetKindClassifier.Classify(fileName).Should().Be(ModelType.LORA);

    /// <summary>A more specific component wins over the family name it belongs to — an LTX VAE is a
    /// VAE, not an LTX LoRA.</summary>
    [Fact]
    public void Classify_PrefersTheComponentOverTheFamily()
    {
        AssetKindClassifier.Classify("LTX23_video_vae_bf16.safetensors")
            .Should().Be(ModelType.VAE);
        AssetKindClassifier.Classify("Wan2_1-T2V-1_3B_FlashVSR_fp32.safetensors")
            .Should().Be(ModelType.LORA, "no marker fires, so the default stands");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_ToleratesAMissingName(string? fileName)
        => AssetKindClassifier.Classify(fileName).Should().Be(ModelType.LORA);

    /// <summary>
    /// Mirrors the AllLabels guards elsewhere in this folder: nothing may come out of here that
    /// ModelTypeExtensions has no name for.
    /// </summary>
    [Fact]
    public void EveryKindItCanReturnIsOneTheAppCanName()
    {
        foreach (var kind in AssetKindClassifier.AllKinds)
        {
            kind.DisplayName().Should().NotBeNullOrWhiteSpace();
            if (kind != ModelType.LORA) kind.SupportFolderName().Should().NotBeNullOrWhiteSpace();
        }
    }
}
