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
    /// Qwen 3 VL 8B - Efficient vision-language model with strong performance.
    /// Uses ChatML prompt format.
    /// </summary>
    Qwen3_VL_8B = 1
}
