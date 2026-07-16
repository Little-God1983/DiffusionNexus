namespace DiffusionNexus.UI.Models;

/// <summary>
/// Represents a LoRA (Low-Rank Adaptation) reference found in a ComfyUI workflow graph.
/// </summary>
public record LoraInfo
{
    public string Name { get; init; } = "";
    public double StrengthModel { get; init; } = 1.0;
    public double StrengthClip { get; init; } = 1.0;

    /// <summary>The loader that emitted this LoRA ("Power Lora", "Lora Stack", "LoraLoader"), or null.</summary>
    public string? Source { get; init; }
}
