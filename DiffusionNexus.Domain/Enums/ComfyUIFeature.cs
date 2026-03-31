namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Identifies each application feature that depends on ComfyUI as its backend execution engine.
/// Used by the unified readiness system to look up the required custom nodes and models per feature.
/// </summary>
public enum ComfyUIFeature
{
    /// <summary>
    /// AI image captioning via the Qwen3-VL ComfyUI workflow.
    /// Workflow: <c>Qwen-3VL-autocaption.json</c>
    /// </summary>
    Captioning,

    /// <summary>
    /// Image inpainting via the Qwen-2512 inpainting workflow in the image editor.
    /// Workflow: <c>Inpaint-Qwen-2512.json</c>
    /// </summary>
    Inpainting,

    /// <summary>
    /// Batch image upscaling with a user-supplied or caption-derived prompt.
    /// Workflow: <c>Z-Image-Turbo-Upscale.json</c>
    /// </summary>
    BatchUpscale,

    /// <summary>
    /// Batch image upscaling with vision-model auto-prompt (Qwen3-VL generates the prompt).
    /// Workflow: <c>Vision-Z-Image-Turbo-Upscale.json</c>
    /// </summary>
    BatchUpscaleVision,

    /// <summary>
    /// Image outpainting (planned). Reserved for future use.
    /// </summary>
    Outpaint
}
