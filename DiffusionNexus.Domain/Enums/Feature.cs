namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Identifies each application feature whose readiness can be checked through the
/// unified <see cref="Services.IFeatureReadinessService"/>. A feature is backend-agnostic:
/// the <see cref="Services.IFeatureBackendRouter"/> decides whether it runs against a
/// ComfyUI server, a local inference engine, or some other backend.
/// </summary>
public enum Feature
{
    /// <summary>
    /// AI image captioning (Qwen3-VL).
    /// ComfyUI workflow: <c>Qwen-3VL-autocaption.json</c>; local backend uses LlamaSharp/MTMD.
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
    /// Image outpainting via the Qwen-2512 outpaint workflow (user-supplied prompt).
    /// Workflow: <c>Qwen-Image-2512-outpaint-nonVision.json</c>
    /// </summary>
    Outpaint,

    /// <summary>
    /// Image outpainting with vision-model auto-prompt (Qwen3-VL describes the surroundings).
    /// Workflow: <c>Qwen-Image-2512-outpaint-Vision.json</c>
    /// </summary>
    OutpaintVision
}
