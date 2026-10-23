using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// Names a file's asset kind from its FILE NAME alone.
/// </summary>
/// <remarks>
/// Name-based and therefore fallible, which is why it is the SECOND rung: a safetensors file is
/// named by its tensor keys (<see cref="AssetKindHeaderMap"/>) and only a container with no
/// readable header — a .pth or .ckpt pickle — is named from here. That is what bounds the risk of
/// a verdict the sorter turns into a physical move.
/// <para>
/// Every marker below is drawn from names observed in a real library rather than invented, and the
/// bar for adding one is that it cannot plausibly occur in a LoRA's own name. That is why
/// <c>clip</c> counts only as the FIRST token (every real encoder observed —
/// <c>CLIP-ViT-H-14-laion2B</c>, <c>clip_g_hidream</c> — leads with it, while a LoRA called
/// <c>hair_clip_v1</c> does not), and why a bare <c>.pth</c> is not treated as an upscaler:
/// <c>Chris.pth</c> in that same library is a perfectly ordinary model.
/// </para>
/// </remarks>
public static class AssetKindClassifier
{
    private static readonly string[] VaeTokens = { "vae" };

    private static readonly string[] TextEncoderTokens =
    {
        "t5", "t5xxl", "umt5", "mistral", "llava", "textencoder",
    };

    // "vl" is two characters and reads perfectly well inside a LoRA name ("anime_vl_style"), so it
    // sits below the bar the class remarks set and cannot stand alone. It counts only alongside the
    // family that names its encoders that way — the real file is "qwen_2.5_vl_7b_fp8_scaled", where
    // "qwen" and "vl" are not adjacent, so a pair rule would not reach it either.
    private const string ShortEncoderToken = "vl";
    private const string ShortEncoderQualifier = "qwen";

    // IP-Adapter is not literally a ControlNet, but it is shelved with them everywhere and no one
    // names a LoRA "ipadapter", so it stays. "redux" was dropped: it fails twice over — Redux is an
    // image-variation adapter rather than a ControlNet, AND Flux Redux LoRAs exist, so the marker
    // could put a wrong chip on a real LoRA.
    private static readonly string[] ControlNetTokens =
    {
        "controlnet", "ipadapter", "instantx",
    };

    // "upscale" was dropped: "hires_upscale_helper", "detail_upscale_v2" are ordinary LoRA names,
    // and it never earned its place — zero files in the reference library carry it as a token,
    // while the longer "upscaler" matches every real upscaler there.
    private static readonly string[] UpscalerTokens =
    {
        "ultrasharp", "esrgan", "realesrgan", "lsdirplus", "lsdir", "swinir", "upscaler",
    };

    /// <summary>
    /// Every kind this classifier can ever return. Test-only seam (DiffusionNexus.Tests,
    /// InternalsVisibleTo) — mirrors <see cref="AssetKindHeaderMap.AllKinds"/>.
    /// </summary>
    internal static IReadOnlyCollection<ModelType> AllKinds { get; } =
        [ModelType.LORA, ModelType.VAE, ModelType.Controlnet, ModelType.TextEncoder, ModelType.Upscaler];

    /// <summary>Asset kind for a file name, defaulting to <see cref="ModelType.LORA"/>.</summary>
    public static ModelType Classify(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return ModelType.LORA;

        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var tokens = stem.Split(['_', '-', '.', ' ', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return ModelType.LORA;

        // Order matters where a name carries two markers: "LTX23_audio_vae_bf16" is a VAE, and
        // "Qwen-Image-InstantX-ControlNet-Inpainting" is a ControlNet, so the more specific
        // component wins over the family name it belongs to.
        if (ContainsAny(tokens, VaeTokens)) return ModelType.VAE;
        if (ContainsAny(tokens, ControlNetTokens)) return ModelType.Controlnet;

        // Leading "clip" only — see class remarks.
        if (string.Equals(tokens[0], "clip", StringComparison.Ordinal)) return ModelType.TextEncoder;
        if (ContainsAny(tokens, TextEncoderTokens)) return ModelType.TextEncoder;
        if (ContainsAny(tokens, [ShortEncoderToken]) && ContainsAny(tokens, [ShortEncoderQualifier]))
            return ModelType.TextEncoder;

        if (ContainsAny(tokens, UpscalerTokens)) return ModelType.Upscaler;

        // "4x-UltraSharp", "4xLSDIRplus", "2xNomosUni" — a scale factor leading the name is an
        // upscaler naming convention and nothing else in a model library is named that way. It
        // appears both as its own token and glued straight onto the model name, so both spellings
        // count.
        if (LeadsWithAScaleFactor(tokens[0])) return ModelType.Upscaler;

        return ModelType.LORA;
    }

    private static bool ContainsAny(string[] tokens, string[] markers)
    {
        foreach (var token in tokens)
        {
            foreach (var marker in markers)
            {
                if (string.Equals(token, marker, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A digit run followed by 'x', then either nothing ("4x", from "4x-UltraSharp") or a remainder
    /// carrying a letter that is not itself another 'x' ("4xlsdirplus"). That letter is what
    /// separates an upscaler from a dimension: "4x4" is a remainder of pure digits and must not
    /// match — nor may "2x2x2", which the plain is-there-a-letter test used to accept because 'x'
    /// is a letter, contradicting the very rule this comment states.
    /// </summary>
    private static bool LeadsWithAScaleFactor(string token)
    {
        var digits = 0;
        while (digits < token.Length && char.IsAsciiDigit(token[digits]))
            digits++;

        if (digits == 0 || digits >= token.Length || token[digits] != 'x')
            return false;

        var remainder = token[(digits + 1)..];
        if (remainder.Length == 0)
            return true;

        foreach (var ch in remainder)
        {
            if (char.IsAsciiLetter(ch) && ch != 'x')
                return true;
        }

        return false;
    }
}
