using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Domain.Loras;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Provides a static entry point for classifying LoRA model variants used throughout the UI layer.
/// </summary>
internal static class LoraVariantClassifier
{
    private static readonly ILoraVariantClassifier Implementation = new DefaultLoraVariantClassifier();

    /// <summary>
    /// Classifies the supplied <see cref="ModelClass"/> instance into a normalized key and variant label.
    /// </summary>
    /// <param name="model">Model metadata to evaluate.</param>
    /// <returns>Classification result used for downstream grouping.</returns>
    public static LoraVariantClassification Classify(ModelClass model) => Implementation.Classify(model);
}
