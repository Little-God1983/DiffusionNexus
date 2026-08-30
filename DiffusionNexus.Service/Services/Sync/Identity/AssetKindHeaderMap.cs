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
    // Normalizing "." to "_" before matching would collapse both spellings into one needle and is
    // deliberately NOT done: the VAE rung's "encoder.down."/"decoder.up.", the TextEncoder rung's
    // "text_model.encoder.layers" and the ".alpha" suffix rule all depend on the dots being real.
    private static readonly string[] LoraNeedles =
    {
        "lora_up", "lora_down", "lora.up", "lora.down", "lora_a.", "lora_b.", "lora_unet", "lora_te",
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
    private static readonly string[] TextEncoderNeedles =
    {
        "text_model.encoder.layers", "token_embedding",
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
