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
/// </remarks>
public static class AssetKindHeaderMap
{
    // Rung 1 — LoRA. Checked first; see class remarks. ".alpha" is matched as a SUFFIX because it
    // is the per-module scale a LoRA writes beside each up/down pair, and as a substring it would
    // hit any tensor whose path merely contains the letters.
    private static readonly string[] LoraNeedles =
    {
        "lora_up", "lora_down", "lora_a.", "lora_b.", "lora_unet", "lora_te",
    };

    private const string LoraAlphaSuffix = ".alpha";

    // Rung 2 — autoencoder. "post_quant_conv"/"quant_conv" are unique to a VAE's latent bottleneck;
    // the down/up block paths are the encoder and decoder stacks either side of it.
    private static readonly string[] VaeNeedles =
    {
        "post_quant_conv", "quant_conv", "encoder.down.", "decoder.up.",
    };

    // Rung 3 — ControlNet. "control_model." is the prefix a bundled ControlNet carries;
    // "controlnet_cond_embedding" and "input_hint_block" are the hint-conditioning stem that only
    // a ControlNet has.
    private static readonly string[] ControlNetNeedles =
    {
        "control_model.", "controlnet_cond_embedding", "input_hint_block",
    };

    // Rung 4 — text encoder. "shared.weight" is T5's tied embedding table; "logit_scale" is CLIP's
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

        foreach (var key in lowered)
        {
            if (key.EndsWith(LoraAlphaSuffix, StringComparison.Ordinal)) return ModelType.LORA;
            if (ContainsAny(key, LoraNeedles)) return ModelType.LORA;
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
