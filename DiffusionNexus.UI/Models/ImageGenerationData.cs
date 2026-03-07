namespace DiffusionNexus.UI.Models;

/// <summary>
/// Holds parsed generation metadata extracted from a ComfyUI-generated PNG image.
/// </summary>
public record ImageGenerationData
{
    public string? FileName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Resolution => $"{Width} × {Height}";

    public string? PositivePrompt { get; init; }
    public string? NegativePrompt { get; init; }

    public string? Checkpoint { get; init; }
    public IReadOnlyList<LoraInfo> Loras { get; init; } = [];

    public string? SamplerName { get; init; }
    public string? Scheduler { get; init; }
    public int? Steps { get; init; }
    public long? Seed { get; init; }
    public double? Cfg { get; init; }
    public double? Denoise { get; init; }

    public bool HasData { get; init; }
    public string? ParseError { get; init; }
}
