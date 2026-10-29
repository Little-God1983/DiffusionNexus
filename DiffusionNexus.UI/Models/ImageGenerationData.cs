namespace DiffusionNexus.UI.Models;

/// <summary>
/// Holds parsed generation metadata extracted from a PNG image.
/// Supports ComfyUI (JSON workflow graph), Automatic1111, and Forge UI (plain-text parameters).
/// </summary>
public record ImageGenerationData
{
    public string? FileName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Resolution => $"{Width} × {Height}";

    public string? PositivePrompt { get; init; }
    public string? NegativePrompt { get; init; }

    /// <summary>
    /// The traced sampler's negative is a ConditioningZeroOut (no text behind it): there is no negative
    /// prompt, which a null <see cref="NegativePrompt"/> alone cannot tell apart from "not found" (#670).
    /// </summary>
    public bool NegativeZeroedOut { get; init; }

    public string? Checkpoint { get; init; }
    public IReadOnlyList<LoraInfo> Loras { get; init; } = [];

    public string? SamplerName { get; init; }
    public string? Scheduler { get; init; }
    public int? Steps { get; init; }
    public long? Seed { get; init; }
    public double? Cfg { get; init; }

    /// <summary>
    /// Flux's built-in ("distilled") guidance, from FluxGuidance / CLIPTextEncodeFlux or the text's
    /// "Distilled CFG Scale" (#674). Separate from <see cref="Cfg"/>, which stays the real CFG.
    /// </summary>
    public double? Guidance { get; init; }
    public double? Denoise { get; init; }

    public bool HasData { get; init; }
    public string? ParseError { get; init; }
}
