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
    /// Diffusers before v0.21 spelled a LoRA's pair with DOTS — <c>lora.down.weight</c> /
    /// <c>lora.up.weight</c> — where kohya and PEFT use underscores. The needle table was
    /// underscore-only, so an old-format LoRA that also trained the text encoder missed the LoRA
    /// rung and fell into the TextEncoder rung, whose "text_model.encoder.layers" needle its keys
    /// match exactly. A real LoRA was then stamped TextEncoder <i>from its weights</i>, which every
    /// guard on this feature trusts — they were built to stop a NAME overriding the weights, not to
    /// stop the weights being read wrong. That row is invisible in the Viewer and unselectable by
    /// any bulk sync, i.e. unrecoverable without hand-editing the database.
    /// </summary>
    [Theory]
    [InlineData("text_encoder.text_model.encoder.layers.0.self_attn.q_proj.lora.down.weight")]
    [InlineData("text_encoder.text_model.encoder.layers.0.self_attn.q_proj.lora.up.weight")]
    [InlineData("unet.down_blocks.0.attentions.0.processor.to_q_lora.down.weight")]
    public void DotSpelledLegacyDiffusersLoraWeightsStillNameALora(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.LORA);

    /// <summary>
    /// The OTHER legacy diffusers spelling, and the one the dotted needles still miss. Its text
    /// encoder half was patched through <c>PatchedLoraProjection</c>, which holds the adapter as an
    /// attribute named <c>lora_linear_layer</c>, so the pair serializes as
    /// "…q_proj.lora_linear_layer.down.weight". That segment sits BETWEEN "lora" and "down", so the
    /// key contains neither "lora_down" nor "lora.down" — it misses rung 1 and matches the
    /// TextEncoder rung's "text_model.encoder.layers" instead. Same bug shape as the dotted one
    /// above, same unrecoverable consequence: a real LoRA stamped TextEncoder from its weights.
    /// </summary>
    [Theory]
    [InlineData("text_encoder.text_model.encoder.layers.0.self_attn.q_proj.lora_linear_layer.down.weight")]
    [InlineData("text_encoder.text_model.encoder.layers.0.self_attn.q_proj.lora_linear_layer.up.weight")]
    public void LegacyDiffusersTextEncoderProjectionKeysStillNameALora(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.LORA);

    /// <summary>
    /// Why the spelling above has to be a needle rather than be left to the file's other keys to
    /// rescue. The same file's UNet half ("unet.…processor.to_q_lora.down.weight") DOES match rung 1
    /// — but <see cref="SafetensorsHeaderReader.MaxSampledTensorKeys"/> caps the sample at the first
    /// 64 root properties in file order, and "text_encoder…" sorts before "unet…", so an
    /// alphabetically written header can hand this map 64 text-encoder keys and not one UNet key.
    /// This fixture is that truncated sample: every key is text-encoder-spelled, exactly as the map
    /// would see it, and there is nothing else in it to fall back on.
    /// </summary>
    [Fact]
    public void ADiffusersLoraSampledEntirelyAtItsTextEncoderHalfIsStillALora()
    {
        var header = Header(
            "text_encoder.text_model.encoder.layers.0.self_attn.k_proj.lora_linear_layer.down.weight",
            "text_encoder.text_model.encoder.layers.0.self_attn.k_proj.lora_linear_layer.up.weight",
            "text_encoder.text_model.encoder.layers.0.self_attn.out_proj.lora_linear_layer.down.weight",
            "text_encoder.text_model.encoder.layers.0.self_attn.q_proj.lora_linear_layer.down.weight",
            "text_encoder.text_model.encoder.layers.0.self_attn.v_proj.lora_linear_layer.down.weight");

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
    /// Final-review Important #3. A full checkpoint is a UNet, a VAE and a text encoder in one
    /// container, and the reader samples only the first
    /// <see cref="SafetensorsHeaderReader.MaxSampledTensorKeys"/> root properties IN FILE ORDER — so
    /// for an alphabetically-keyed checkpoint that whole sample can be "cond_stage_model.…", which
    /// would hit the TextEncoder rung and file somebody's checkpoint into <c>Text Encoder\</c>.
    /// The keys here are in exactly that hostile order: the encoder-shaped key comes FIRST, so the
    /// composite guard placed anywhere below the KIND-answering rungs would already have been beaten
    /// to it. Its one legitimate position is the one it has — after the LoRA rung, which answers no
    /// kind for a checkpoint at all, and before every other one.
    /// </summary>
    [Fact]
    public void ACompositeCheckpointSaysNothingRatherThanNamingOneOfItsParts()
    {
        var header = Header(
            "cond_stage_model.transformer.text_model.encoder.layers.0.self_attn.q_proj.weight",
            "model.diffusion_model.input_blocks.0.0.weight");

        AssetKindHeaderMap.Map(header).Should().BeNull();
    }

    /// <summary>
    /// The other sampling order the same file can present, hitting a different wrong rung: the
    /// autoencoder half of the checkpoint lands in the sample instead of the encoder half.
    /// </summary>
    [Fact]
    public void ACompositeCheckpointSampledAtItsAutoencoderHalfStillSaysNothing()
    {
        var header = Header(
            "first_stage_model.encoder.down.0.block.0.norm1.weight",
            "first_stage_model.post_quant_conv.weight");

        AssetKindHeaderMap.Map(header).Should().BeNull();
    }

    /// <summary>
    /// The other half of the composite guard's ordering, and the reason it sits BELOW the LoRA rung
    /// rather than above it. The risk is one-sided. A LoRA saved in the checkpoint-prefixed layout
    /// carries both a "model.diffusion_model." path and its own up/down pair, and with the guard
    /// first it lost its weights verdict and fell to the name rung — a guess, which is the one thing
    /// this class exists to pre-empt. The reverse cannot happen: a genuine composite checkpoint
    /// carries no lora_up / lora_te / ".alpha" key for the LoRA rung to mis-fire on, which is what
    /// the two tests above assert.
    /// </summary>
    [Theory]
    [InlineData("model.diffusion_model.input_blocks.1.1.transformer_blocks.0.attn1.to_q.lora_up.weight")]
    [InlineData("model.diffusion_model.output_blocks.5.1.transformer_blocks.0.attn2.to_k.alpha")]
    public void ALoraKeyedLikeACheckpointIsStillALora(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.LORA);

    /// <summary>
    /// The composite guard must only excuse the map from composite containers, never from ordinary
    /// ones: an ordinary LoRA carries none of the checkpoint prefixes, and ComfyUI-format LoRAs spell
    /// theirs "diffusion_model." with no "model." ahead of it — which is why the needle keeps its
    /// prefix.
    /// </summary>
    [Theory]
    [InlineData("diffusion_model.double_blocks.0.img_attn.qkv.lora_a.weight")]
    [InlineData("lora_unet_single_blocks_0_linear1.lora_up.weight")]
    public void TheCompositeGuardDoesNotReachAnOrdinaryLora(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.LORA);

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

    // ---------------------------------------------------------------------------------------
    // Manual-smoke coverage: a real library's TextEncoders\ folder, 29 safetensors containers,
    // every fixture below transcribed verbatim from that file's own header.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Group A — the HuggingFace causal-LM layout that ComfyUI now ships as a prompt encoder.
    /// Sixteen real files in that folder carry it and not one matched a needle here, so the map
    /// said nothing; the name rung has no marker for "gemma" / "llama" / "qwen3vl" / "ernie" /
    /// "ministral" either, so every one of them fell to <see cref="AssetKindClassifier"/>'s LORA
    /// default. A text encoder filed as a LoRA is the mirror of the bug the dotted-spelling tests
    /// above fix, and it pollutes the Viewer instead of emptying it.
    /// Keys verbatim from <c>gemma_3_12B_it.safetensors</c> (1066 tensors).
    /// </summary>
    [Fact]
    public void AGemmaDecoderSampleNamesATextEncoder()
    {
        var header = Header(
            "model.embed_tokens.weight",
            "model.layers.0.input_layernorm.weight",
            "model.layers.0.mlp.down_proj.weight",
            "model.layers.0.mlp.gate_proj.weight",
            "model.layers.0.mlp.up_proj.weight",
            "model.layers.0.post_attention_layernorm.weight");

        AssetKindHeaderMap.Map(header).Should().Be(ModelType.TextEncoder);
    }

    /// <summary>
    /// The same layout with every tensor replaced by its fp8 scale, so the sample carries no
    /// embedding table at all and <c>mlp.gate_proj</c> is the only needle left standing. Keys
    /// verbatim from <c>llama_3.1_8b_instruct_fp8_scaled.safetensors</c> (516 tensors).
    /// </summary>
    [Fact]
    public void AnFp8ScaledLlamaSampleNamesATextEncoder()
    {
        var header = Header(
            "model.layers.0.mlp.down_proj.scale_weight",
            "model.layers.0.mlp.gate_proj.scale_weight",
            "model.layers.0.mlp.up_proj.scale_weight",
            "model.layers.0.self_attn.k_proj.scale_weight",
            "model.layers.0.self_attn.o_proj.scale_weight",
            "model.layers.0.self_attn.q_proj.scale_weight");

        AssetKindHeaderMap.Map(header).Should().Be(ModelType.TextEncoder);
    }

    /// <summary>
    /// The converse of the case above, and why <c>model.embed_tokens</c> is not redundant with
    /// <c>mlp.gate_proj</c>: this file's first 64 keys in file order contain NO gate projection —
    /// its fp8 cast left the layernorms unquantized, so they sort ahead of the mlp block — and the
    /// embedding table is the only thing in the sample left to match. Keys verbatim from
    /// <c>gemma_3_12B_it_fp8_e4m3fn.safetensors</c> (1066 tensors).
    /// </summary>
    [Fact]
    public void ADecoderSampleWithNoGateProjectionIsStillNamedByItsEmbeddingTable()
    {
        var header = Header(
            "model.embed_tokens.weight",
            "model.layers.0.input_layernorm.weight",
            "model.layers.0.post_attention_layernorm.weight",
            "model.layers.0.post_feedforward_layernorm.weight",
            "model.layers.0.pre_feedforward_layernorm.weight",
            "model.layers.0.self_attn.k_norm.weight");

        AssetKindHeaderMap.Map(header).Should().Be(ModelType.TextEncoder);
    }

    /// <summary>
    /// The vision-language spelling: a multimodal container puts the decoder under
    /// <c>language_model.</c> instead of <c>model.</c>, so <c>model.embed_tokens</c> misses it
    /// outright. Keys verbatim from <c>qwen3vl_8b_fp8-nf4.safetensors</c> (1853 tensors), whose
    /// nf4 quantization also strips every plain <c>.weight</c> down to an absmax/quant_map pair.
    /// </summary>
    [Fact]
    public void ALanguageModelPrefixedDecoderSampleNamesATextEncoder()
    {
        var header = Header(
            "language_model.layers.0.mlp.down_proj.weight.absmax",
            "language_model.layers.0.mlp.down_proj.weight.quant_map",
            "language_model.layers.0.mlp.gate_proj.weight.absmax",
            "language_model.layers.0.mlp.gate_proj.weight.quant_map",
            "language_model.layers.0.mlp.up_proj.weight.absmax",
            "language_model.layers.0.mlp.up_proj.weight.quant_map");

        AssetKindHeaderMap.Map(header).Should().Be(ModelType.TextEncoder);
    }

    /// <summary>
    /// Each LLM-decoder needle standing alone, so an edit that drops one fails here rather than
    /// only on whichever real file happened to carry it.
    /// </summary>
    [Theory]
    [InlineData("model.layers.0.mlp.gate_proj.weight")]
    [InlineData("model.embed_tokens.weight")]
    [InlineData("language_model.layers.0.self_attn.q_proj.weight")]
    [InlineData("lm_head.weight")]
    public void LlmDecoderWeightsNameATextEncoder(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.TextEncoder);

    /// <summary>
    /// The LLM needles sit in the NORMAL TextEncoder rung, BELOW the composite guard, and this is
    /// what that placement buys: a full checkpoint that BUNDLES an LLM encoder — HiDream bundles
    /// Llama — carries both a checkpoint prefix and decoder keys, and must still be excused rather
    /// than named after whichever part of itself leads the sample. None of the sixteen real Group A
    /// files carries a composite prefix in ANY of its keys, so the ordering costs them nothing.
    /// </summary>
    [Fact]
    public void ACheckpointBundlingAnLlmEncoderIsStillExcused()
    {
        var header = Header(
            "cond_stage_model.model.embed_tokens.weight",
            "model.diffusion_model.double_blocks.0.img_attn.qkv.weight");

        AssetKindHeaderMap.Map(header).Should().BeNull();
    }

    /// <summary>
    /// The verdicts the smoke found already correct, pinned so this change cannot move them. The
    /// CLIP files match on header; the T5 family does NOT — their sampled keys are
    /// <c>encoder.block.…</c> or <c>blocks.0.attn.…</c>, which no needle here reaches (widening to a
    /// bare "encoder.block." would sit one path segment from the VAE rung's "encoder.down."), so
    /// they are named from their file names and must keep saying nothing here.
    /// </summary>
    [Theory]
    // clip_l / clip_g_sdxl_base / clip_g_hidream / clip_l_hidream / ViT-L-14-…-TE-only-HF
    [InlineData(ModelType.TextEncoder, "text_model.embeddings.position_embedding.weight", "text_model.encoder.layers.0.layer_norm1.bias")]
    // t5xxl_fp16 / google_t5-v1_1-xxl_encoderonly-fp8_e4m3fn / umt5_xxl_fp8_e4m3fn_scaled
    [InlineData(null, "encoder.block.0.layer.0.SelfAttention.k.weight", "encoder.block.0.layer.0.layer_norm.weight")]
    // umt5-xxl-enc-bf16
    [InlineData(null, "blocks.0.attn.k.weight", "blocks.0.ffn.fc1.weight")]
    public void TheVerdictsTheSmokeFoundCorrectAreUnchanged(ModelType? expected, string first, string second)
        => AssetKindHeaderMap.Map(Header(first, second)).Should().Be(expected);
}
