namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Supported vision-language models for local AI image captioning.
/// </summary>
public enum CaptioningModelType
{
    /// <summary>
    /// LLaVA v1.6 34B - High quality vision-language model.
    /// Uses Vicuna prompt format.
    /// </summary>
    LLaVA_v1_6_34B = 0,

    /// <summary>
    /// Qwen 2.5 VL 7B - Efficient vision-language model with strong performance.
    /// Uses ChatML prompt format.
    /// </summary>
    Qwen2_5_VL_7B = 1,

    /// <summary>
    /// Qwen 3 VL 8B - Most powerful Qwen vision-language model.
    /// Features 256K context, visual agent capabilities, 3D grounding, 32-language OCR.
    /// Uses ChatML prompt format.
    /// </summary>
    Qwen3_VL_8B = 2,

    // Enum value 3 (Qwen3_VL_8B_Abliterated_Q8) was the previous "user-supplied
    // file drop" entry. Removed once the Abliterated Caption-it fine-tune
    // became a proper VRAM-tiered downloadable workload — any plain abliterated
    // weights on disk are still picked up by the manager's recursive path scan,
    // they just appear as the matching tiered entry's Ready state if the
    // filename happens to align. The enum value is intentionally retired (not
    // reused) so old persisted selections don't silently land on a different
    // model.

    /// <summary>
    /// Qwen 3 VL 8B - Abliterated Caption-it. General-purpose uncensored
    /// captioning fine-tune on top of the abliterated base model. Downloadable
    /// from mradermacher with VRAM-tier-aware quantization selection (Q4_K_M
    /// through Q8_0 plus matching mmproj projector).
    /// </summary>
    Qwen3_VL_8B_Abliterated_Caption = 4,

    /// <summary>
    /// Qwen 3 VL 8B - NSFW Caption V4. NSFW-specialised fine-tune of the same
    /// Qwen3-VL-8B base (sibling lineage to Abliterated_Caption, not a
    /// successor). Downloadable from mradermacher with VRAM-tier-aware
    /// quantization selection.
    /// </summary>
    Qwen3_VL_8B_NSFW_Caption_V4 = 5
}
