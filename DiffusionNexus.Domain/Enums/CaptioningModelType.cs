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
    Qwen3_VL_8B = 2
}
