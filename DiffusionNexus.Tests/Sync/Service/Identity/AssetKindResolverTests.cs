using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// The precedence rule, in one place because three callers depend on it: discovery, the identify
/// step, and the sorter's own resolver.
/// </summary>
public sealed class AssetKindResolverTests
{
    private static SafetensorsHeaderInfo Header(params string[] keys) => new(null, null, null, keys);

    /// <summary>
    /// The whole point of reading the weights. The issue named this exact hazard: a LoRA called
    /// "vae_finetune_lora" is a LoRA, and the name rung must never get to see it.
    /// </summary>
    [Fact]
    public void AHeaderProvingALoraBeatsAFileNameSayingVae()
        => AssetKindResolver.Resolve(
                Header("lora_unet_blocks_0.lora_up.weight"),
                "vae_finetune_lora.safetensors")
            .Should().Be(ModelType.LORA);

    [Fact]
    public void AHeaderProvingAVaeBeatsAnUninformativeName()
        => AssetKindResolver.Resolve(Header("post_quant_conv.weight"), "BRFHE7KV2VWXY8N3D4SXR4XCT0.safetensors")
            .Should().Be(ModelType.VAE);

    /// <summary>
    /// A .pth pickle has no readable header, so the name is all there is — which is why the name
    /// rung still exists and why every real upscaler in the reference library is a .pth.
    /// </summary>
    [Fact]
    public void WithNoReadableHeaderTheNameDecides()
        => AssetKindResolver.Resolve(header: null, "4x-UltraSharp.pth").Should().Be(ModelType.Upscaler);

    /// <summary>
    /// A header that parsed but recognizes nothing is not an answer — fall through to the name
    /// rather than treat "the keys said nothing" as "it is a LoRA".
    /// </summary>
    [Fact]
    public void AnUnrecognizedHeaderFallsThroughToTheName()
        => AssetKindResolver.Resolve(Header("model.diffusion_model.input_blocks.0.0.weight"),
                "Wan2_2_VAE_bf16.safetensors")
            .Should().Be(ModelType.VAE);

    [Fact]
    public void WhenNothingSaysAnythingItIsALora()
        => AssetKindResolver.Resolve(header: null, "MyChar_Pony_v2.safetensors").Should().Be(ModelType.LORA);

    [Fact]
    public async Task ResolveAsyncReadsARealFileHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("post_quant_conv.weight")));

        try
        {
            (await AssetKindResolver.ResolveAsync(path)).Should().Be(ModelType.VAE);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file that cannot be opened at all must not throw into a discovery loop — it is simply a
    /// file whose weights we could not read, which is what the name rung is for.
    /// </summary>
    [Fact]
    public async Task ResolveAsyncFallsBackToTheNameWhenTheFileIsUnreadable()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "4xLSDIRplus.pth");

        (await AssetKindResolver.ResolveAsync(missing)).Should().Be(ModelType.Upscaler);
    }
}
