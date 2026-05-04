namespace DiffusionNexus.Inference.Abstractions;

/// <summary>
/// Reference image attached to a diffusion request. Currently only carries the file path;
/// a future revision will accept in-memory pixels for canvas-painted inputs.
/// </summary>
/// <param name="FilePath">Absolute path to a PNG/JPEG file on disk.</param>
/// <param name="Strength">Conditioning strength (0–1). Interpretation depends on usage (init image, ref image, mask, etc.).</param>
public sealed record DiffusionReferenceImage(string FilePath, float Strength = 1.0f);

/// <summary>
/// Identifies a LoRA to load over the base diffusion model.
/// </summary>
/// <param name="FilePath">Absolute path to the .safetensors LoRA file.</param>
/// <param name="Strength">Model strength (typically 0–1.5).</param>
public sealed record LoraReference(string FilePath, float Strength = 1.0f);

/// <summary>
/// All inputs for a single image generation. The shape is intentionally wider than v1
/// honors — fields like <see cref="NegativePrompt"/>, <see cref="Loras"/>, <see cref="ControlNets"/>,
/// <see cref="InitImage"/> and <see cref="MaskImage"/> are present today so caller code does not
/// need to change when backends start honoring them.
/// </summary>
public sealed class DiffusionRequest
{
    /// <summary>The model descriptor key (matches <c>ModelDescriptor.Key</c>) selecting which model to use.</summary>
    public required string ModelKey { get; init; }

    /// <summary>The positive prompt.</summary>
    public required string Prompt { get; init; }

    /// <summary>Output width in pixels. Must satisfy the model's <c>DimensionAlignment</c>.</summary>
    public required int Width { get; init; }

    /// <summary>Output height in pixels. Must satisfy the model's <c>DimensionAlignment</c>.</summary>
    public required int Height { get; init; }

    /// <summary>Number of sampling steps. <c>null</c> uses the model's default.</summary>
    public int? Steps { get; init; }

    /// <summary>Classifier-free guidance scale. <c>null</c> uses the model's default.</summary>
    public float? Cfg { get; init; }

    /// <summary>Sampler name (e.g. "euler"). <c>null</c> uses the model's default.</summary>
    public string? Sampler { get; init; }

    /// <summary>Scheduler name (e.g. "simple"). <c>null</c> uses the model's default.</summary>
    public string? Scheduler { get; init; }

    /// <summary>RNG seed. <c>null</c> picks a random one and reports it back via <c>DiffusionResult.Seed</c>.</summary>
    public long? Seed { get; init; }

    // TODO(v2-negative-prompt): Backend currently ignores this field — placeholder for the negative-prompt UI control.
    /// <summary>Negative prompt. <b>Currently ignored by the v1 backend.</b></summary>
    public string? NegativePrompt { get; init; }

    // TODO(v2-loras): Backend currently ignores this collection — placeholder for the LoRA picker.
    /// <summary>LoRAs to load over the base model. <b>Currently ignored by the v1 backend.</b></summary>
    public IReadOnlyList<LoraReference> Loras { get; init; } = [];

    // TODO(v2-controlnet): Backend currently ignores this collection — placeholder for ControlNet inputs.
    /// <summary>ControlNet conditioning inputs. <b>Currently ignored by the v1 backend.</b></summary>
    public IReadOnlyList<DiffusionReferenceImage> ControlNets { get; init; } = [];

    // TODO(v2-img2img): Backend currently ignores this — placeholder for the img2img / canvas init-image flow.
    /// <summary>Initial image for img2img. <b>Currently ignored by the v1 backend.</b></summary>
    public DiffusionReferenceImage? InitImage { get; init; }

    // TODO(v2-inpaint): Backend currently ignores this — placeholder for inpaint mask painting.
    /// <summary>Mask for inpainting (white = repaint, black = keep). <b>Currently ignored by the v1 backend.</b></summary>
    public DiffusionReferenceImage? MaskImage { get; init; }
}
