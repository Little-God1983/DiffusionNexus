using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// Names what a safetensors container actually is from its TENSOR KEYS — a reading of the weights,
/// not a guess about the file name.
/// </summary>
/// <remarks>
/// This is the rung that makes #527 safe to act on. The sorter physically relocates files off this
/// verdict, and a LoRA called <c>vae_finetune_lora</c> must not be filed as a VAE because of how
/// its author named it. The keys cannot lie about this: a LoRA carries <c>lora_up</c>/<c>lora_A</c>
/// pairs, an autoencoder carries <c>post_quant_conv</c>, a text encoder carries
/// <c>text_model.encoder.layers</c>.
/// <para>
/// There is deliberately no upscaler rung: ESRGAN-family upscalers ship as <c>.pth</c> pickles with
/// no readable header at all, so a rule for them here would never fire. They are named by
/// <see cref="AssetKindClassifier"/> instead.
/// </para>
/// <para>
/// Order is load-bearing. A LoRA trained on the text encoder carries both LoRA markers and
/// encoder-shaped key names, so the LoRA evidence is checked first: what the file IS outranks what
/// it was trained on, exactly as <see cref="BaseModelHeaderMap"/> checks its name hint before the
/// architecture that every SDXL refinement shares.
/// </para>
/// <para>
/// Every rung BELOW the LoRA rung assumes ONE purpose per container, so a composite-container guard
/// sits directly beneath it and excuses this map from the files where that assumption does not hold
/// — a full checkpoint, which bundles a UNet, a VAE and a text encoder together. It answers null
/// rather than a kind; see the comment on it for why. It sits below the LoRA rung and not above it
/// because the risk is one-sided: a LoRA whose keys happen to carry a checkpoint-shaped prefix would
/// lose its weights verdict and be handed to the name rung, while the reverse cannot happen — a
/// genuine composite checkpoint carries no <c>lora_up</c>, <c>lora_te</c> or <c>.alpha</c> keys, so
/// the LoRA rung has nothing to mis-fire on.
/// </para>
/// </remarks>
public static class AssetKindHeaderMap
{
    // Rung 1 — LoRA. Checked first; see class remarks. ".alpha" is matched as a SUFFIX because it
    // is the per-module scale a LoRA writes beside each up/down pair, and as a substring it would
    // hit any tensor whose path merely contains the letters.
    //
    // Both SPELLINGS of the up/down pair are needed. kohya and PEFT write "lora_up"/"lora_down";
    // diffusers before v0.21 wrote "lora.down.weight"/"lora.up.weight" with dots, and its attention
    // processors spelled them "…to_q_lora.down.weight". Missing the dotted form was not a missed
    // detection but a WRONG one: an old-format LoRA that also trained the text encoder carries
    // "text_encoder.text_model.encoder.layers.…lora.down.weight", so it fell past this rung into the
    // TextEncoder rung, whose needle those keys match — a real LoRA stamped TextEncoder from its
    // weights, which every guard on this feature trusts, leaving it invisible in the Viewer and
    // unselectable by any bulk sync.
    //
    // There is no dotted A/B pair to add: the A/B naming arrives with PEFT's lora_A/lora_B module
    // dict, which serializes with underscores, while the dot-spelled legacy format only ever named
    // its pair up/down. A needle for a spelling no tool writes is a needle that can only misfire.
    //
    // "lora_linear_layer" is a THIRD spelling and is NOT redundant with either pair — do not remove
    // it as such. Legacy diffusers patched the text encoder through PatchedLoraProjection, which
    // holds the adapter as an attribute of that name, so its keys read
    // "…q_proj.lora_linear_layer.down.weight": the segment sits BETWEEN "lora" and "down", so the
    // key contains neither "lora_down" nor "lora.down" and both pairs above miss it. It then matches
    // the TextEncoder rung, stamping a real LoRA TextEncoder from its weights.
    //
    // The file's own UNet keys ("unet.…processor.to_q_lora.down.weight") would otherwise rescue it,
    // but they cannot be relied on: SafetensorsHeaderReader samples only the first
    // MaxSampledTensorKeys root properties in file order, and "text_encoder…" sorts before "unet…",
    // so an alphabetically written header can present 64 text-encoder keys and no UNet key at all.
    // Same sampling hazard the composite guard below exists for, reached from the other side.
    //
    // Normalizing "." to "_" before matching would collapse the two up/down spellings into one
    // needle (it would not help this one) and is deliberately NOT done: the VAE rung's
    // "encoder.down."/"decoder.up.", the TextEncoder rung's "text_model.encoder.layers" and the
    // ".alpha" suffix rule all depend on the dots being real.
    private static readonly string[] LoraNeedles =
    {
        "lora_up", "lora_down", "lora.up", "lora.down", "lora_linear_layer",
        "lora_a.", "lora_b.", "lora_unet", "lora_te",
    };

    private const string LoraAlphaSuffix = ".alpha";

    // Rung 2 — composite container. Every rung below assumes ONE purpose per file, and a full
    // checkpoint breaks that assumption outright: it is a UNet, a VAE and a text encoder in one
    // container. SafetensorsHeaderReader samples only the first MaxSampledTensorKeys root
    // properties in file order, so for an alphabetically-keyed checkpoint that sample can be
    // entirely "cond_stage_model.transformer.text_model.…" — which hits the TextEncoder rung — while
    // another ordering lands the "first_stage_model.…encoder.down." block and hits VAE. Both are
    // confident, both are wrong, and either would move the user's checkpoint into a support-asset
    // folder.
    //
    // These three prefixes are the CompVis/A1111 state-dict layout that only a bundled checkpoint
    // has; nothing that is only a VAE, only an encoder, or only a LoRA carries them (ComfyUI-format
    // LoRAs use a bare "diffusion_model." with no "model." ahead of it, which is why the needle
    // keeps its prefix).
    private static readonly string[] CompositeCheckpointNeedles =
    {
        "model.diffusion_model.", "first_stage_model.", "cond_stage_model.",
    };

    // Rung 3 — autoencoder. "post_quant_conv"/"quant_conv" are unique to a VAE's latent bottleneck;
    // the down/up block paths are the encoder and decoder stacks either side of it.
    private static readonly string[] VaeNeedles =
    {
        "post_quant_conv", "quant_conv", "encoder.down.", "decoder.up.",
    };

    // Rung 4 — ControlNet. "control_model." is the prefix a bundled ControlNet carries;
    // "controlnet_cond_embedding" and "input_hint_block" are the hint-conditioning stem that only
    // a ControlNet has.
    private static readonly string[] ControlNetNeedles =
    {
        "control_model.", "controlnet_cond_embedding", "input_hint_block",
    };

    // Rung 5 — text encoder. "shared.weight" is T5's tied embedding table; "logit_scale" is CLIP's
    // learned temperature. Both are single, whole keys rather than path fragments, so they are
    // matched exactly — "shared.weight" as a substring would hit unrelated paths.
    //
    // The second group is the HuggingFace CAUSAL-LM layout, which the first group does not reach at
    // all: a decoder-only LLM shipped as a prompt encoder (Gemma, Llama, Qwen 3 / Qwen-VL, Mistral,
    // ERNIE) writes "model.layers.N.…", never "text_model.encoder.layers", and carries neither
    // logit_scale nor shared.weight. Sixteen such files sat in one real library's TextEncoders\
    // folder and every one of them fell past this rung, past the name rung — which has markers for
    // "t5"/"umt5"/"mistral" but none for gemma, llama, qwen3vl, ernie or ministral — and landed on
    // AssetKindClassifier's LORA default.
    //
    // "mlp.gate_proj" is the strongest of the four and carries fifteen of those sixteen on their
    // real sampled keys: it is the SwiGLU gate every model in that family has and nothing in a
    // U-Net, a VAE, a ControlNet or a CLIP/T5 encoder is spelled that way. It survives quantization,
    // which matters more than it sounds — an fp8 or nf4 cast replaces every plain ".weight" with
    // ".scale_weight"/".weight_scale"/".absmax", so needles anchored on the tensor's SUFFIX die
    // while a needle anchored on the MODULE PATH does not.
    //
    // "model.embed_tokens" is not redundant with it. gemma_3_12B_it_fp8_e4m3fn.safetensors is the
    // sixteenth file: its cast left the layernorms unquantized, so they sort ahead of the mlp block
    // and its first 64 keys in file order contain no gate projection at all. The embedding table is
    // the only thing in that sample to match, so this needle is the one that catches it.
    //
    // "language_model.layers." is the multimodal spelling — a VL container nests the decoder under
    // "language_model." rather than "model.", so "model.embed_tokens" misses it outright — and
    // "lm_head." is the tied output head. Neither is load-bearing on the observed library
    // (qwen3vl_8b_fp8-nf4 carries the "language_model." spelling but its sample also has a gate
    // projection, and no real file's first 64 keys reach its lm_head at all). They are here because
    // the sample is the first 64 root properties IN FILE ORDER and nothing guarantees which slice of
    // a decoder that is: both are keys the family genuinely writes, so unlike a needle for a
    // spelling no tool emits, they can be right without ever being wrong.
    //
    // These belong in the NORMAL rung, below the composite guard, and that is load-bearing. A full
    // checkpoint that BUNDLES an LLM encoder — HiDream bundles Llama — carries decoder keys AND a
    // checkpoint prefix, and the guard running first is what stops it being named after one of its
    // parts. It costs the sixteen real files nothing: not one of them carries a composite prefix in
    // any of its keys.
    private static readonly string[] TextEncoderNeedles =
    {
        "text_model.encoder.layers", "token_embedding",
        "mlp.gate_proj", "model.embed_tokens", "language_model.layers.", "lm_head.",
    };

    private static readonly string[] TextEncoderExactKeys =
    {
        "logit_scale", "shared.weight",
    };

    /// <summary>
    /// Every kind this map can ever return. Test-only seam (DiffusionNexus.Tests,
    /// InternalsVisibleTo) — mirrors <see cref="BaseModelHeaderMap.AllLabels"/>.
    /// </summary>
    internal static IReadOnlyCollection<ModelType> AllKinds { get; } =
        [ModelType.LORA, ModelType.VAE, ModelType.Controlnet, ModelType.TextEncoder];

    /// <summary>What the tensor keys say this file is, or null when they say nothing usable.</summary>
    public static ModelType? Map(SafetensorsHeaderInfo? info)
    {
        if (info is null) return null;

        var keys = info.Keys;
        if (keys.Count == 0) return null;

        var lowered = new string[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            lowered[i] = keys[i].ToLowerInvariant();

        // Rung 1 — LoRA, and it runs before the composite guard below, which the class remarks call
        // load-bearing in BOTH directions. What a file IS outranks what it was trained on, so a
        // LoRA trained on the text encoder is a LoRA and never a text encoder. And the ordering
        // against rung 2 is safe only this way round, because the risk there is one-sided: a LoRA
        // whose keys happen to carry a checkpoint-shaped prefix would lose its weights verdict to a
        // guard meant for bundled checkpoints and fall to the name rung — the guess this whole class
        // exists to pre-empt — whereas a genuine composite checkpoint carries no lora_up, lora_te or
        // ".alpha" keys, so this rung cannot mis-fire on one.
        foreach (var key in lowered)
        {
            if (key.EndsWith(LoraAlphaSuffix, StringComparison.Ordinal)) return ModelType.LORA;
            if (ContainsAny(key, LoraNeedles)) return ModelType.LORA;
        }

        // Rung 2 answers NULL, never a ModelType — deliberately. Returning ModelType.Checkpoint
        // here would be a truer statement about the file and a worse thing to do: it would create a
        // second class of row that silently vanishes from the Viewer (ModelFileSyncService's
        // IsLoraFamily) and from every bulk sync (SyncStateRepository's LoraFamily filter), which is
        // exactly the disappearance #527's §5 exists to stop causing. Null falls through to the name
        // rung, which is where a checkpoint has always been decided, so this rung only ever REMOVES
        // a wrong confident answer.
        foreach (var key in lowered)
        {
            if (ContainsAny(key, CompositeCheckpointNeedles)) return null;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, VaeNeedles)) return ModelType.VAE;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, ControlNetNeedles)) return ModelType.Controlnet;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, TextEncoderNeedles)) return ModelType.TextEncoder;
            foreach (var exact in TextEncoderExactKeys)
            {
                if (string.Equals(key, exact, StringComparison.Ordinal)) return ModelType.TextEncoder;
            }
        }

        return null;
    }

    private static bool ContainsAny(string key, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (key.Contains(needle, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
