using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.Domain.Loras;

/// <summary>
/// Represents the result of classifying a <see cref="ModelClass"/> into a normalized key and variant label.
/// </summary>
/// <param name="NormalizedKey">Canonical identifier for the LoRA derived from file metadata.</param>
/// <param name="VariantLabel">Human readable label describing the detected variant (for example High or Low).</param>
internal sealed record LoraVariantClassification(string NormalizedKey, string? VariantLabel);
