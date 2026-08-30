using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Every key pattern here is one a real container carries. This is the rung that makes the whole
/// feature safe to act on: the sorter physically MOVES files off this verdict, and a name-based
/// guess is not a good enough reason to move somebody's weights.
/// </summary>
public sealed class AssetKindHeaderMapTests
{
    private static SafetensorsHeaderInfo Header(params string[] keys) => new(null, null, null, keys);

    [Theory]
    [InlineData("lora_unet_single_blocks_0_linear1.lora_up.weight")]
    [InlineData("lora_unet_single_blocks_0_linear1.lora_down.weight")]
    [InlineData("lora_te_text_model_encoder_layers_0_mlp_fc1.lora_up.weight")]
    [InlineData("transformer.blocks.0.attn.to_q.lora_A.weight")]
    [InlineData("transformer.blocks.0.attn.to_q.lora_B.weight")]
    [InlineData("lora_unet_single_blocks_0_linear1.alpha")]
    public void LoraWeightsNameALora(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.LORA);

    [Theory]
    [InlineData("post_quant_conv.weight")]
    [InlineData("quant_conv.bias")]
    [InlineData("encoder.down.0.block.0.norm1.weight")]
    [InlineData("decoder.up.3.block.2.conv2.bias")]
    public void AutoencoderWeightsNameAVae(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.VAE);

    [Theory]
    [InlineData("text_model.encoder.layers.0.self_attn.q_proj.weight")]
    [InlineData("logit_scale")]
    [InlineData("text_model.embeddings.token_embedding.weight")]
    [InlineData("shared.weight")]
    public void EncoderWeightsNameATextEncoder(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.TextEncoder);

    [Theory]
    [InlineData("control_model.input_blocks.0.0.weight")]
    [InlineData("controlnet_cond_embedding.conv_in.weight")]
    [InlineData("input_hint_block.0.weight")]
    public void ControlWeightsNameAControlNet(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.Controlnet);

    /// <summary>
    /// A header whose keys match nothing must say nothing, so the caller falls through to the
    /// file name rather than being handed a confident wrong answer.
    /// </summary>
    [Theory]
    [InlineData("model.diffusion_model.input_blocks.0.0.weight")]
    [InlineData("some.opaque.tensor")]
    public void UnrecognizedWeightsSayNothing(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().BeNull();

    [Fact]
    public void ANullHeaderSaysNothing()
        => AssetKindHeaderMap.Map(null).Should().BeNull();

    [Fact]
    public void AHeaderWithNoTensorsSaysNothing()
        => AssetKindHeaderMap.Map(new SafetensorsHeaderInfo(null, null, null)).Should().BeNull();

    /// <summary>
    /// A LoRA trained on a text encoder carries BOTH "lora_te_" and "text_model.encoder.layers"
    /// shaped keys. It is a LoRA — that is what the file is — so the LoRA evidence has to be
    /// checked before the encoder evidence, not merely be present in the table.
    /// </summary>
    [Fact]
    public void ATextEncoderLoraIsALoraNotATextEncoder()
    {
        var header = Header(
            "lora_te_text_model_encoder_layers_0_mlp_fc1.lora_up.weight",
            "lora_te_text_model_encoder_layers_0_mlp_fc1.lora_down.weight");

        AssetKindHeaderMap.Map(header).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// The guard for the rung ORDER itself, which the class remarks call load-bearing. The key
    /// order here is the whole point: a TextEncoder-only key comes FIRST, a LoRA-only key SECOND.
    /// Four sequential per-rung passes answer LoRA — the LoRA rung scans every key before the
    /// encoder rung runs at all. A single pass that checked all four rungs per key would answer
    /// TextEncoder on the first key and never reach the second.
    /// </summary>
    /// <remarks>
    /// This case exists because the sibling test above cannot serve as the guard: kohya writes
    /// "text_model_encoder_layers" with underscores, which never matches the dotted
    /// "text_model.encoder.layers" needle, so both of its keys are LoRA-only and it passes under
    /// either implementation.
    /// </remarks>
    [Fact]
    public void TheLoraRungScansEveryKeyBeforeTheEncoderRungRunsAtAll()
    {
        var header = Header(
            "text_model.encoder.layers.0.self_attn.q_proj.weight",
            "lora_unet_single_blocks_0_linear1.lora_up.weight");

        AssetKindHeaderMap.Map(header).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// Mirrors the AllLabels guards on BaseModelHeaderMap and FilenameBaseModelHeuristic: nothing
    /// may be returned from here that the rest of the app has no name for.
    /// </summary>
    [Fact]
    public void EveryKindItCanReturnIsOneTheAppCanName()
    {
        foreach (var kind in AssetKindHeaderMap.AllKinds)
        {
            kind.DisplayName().Should().NotBeNullOrWhiteSpace();
            if (kind != ModelType.LORA) kind.IsSupportAsset().Should().BeTrue();
        }
    }
}
