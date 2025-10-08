using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.Domain.Loras;

/// <summary>
/// Provides functionality for classifying LoRA models into normalized identifiers and variant labels.
/// </summary>
internal interface ILoraVariantClassifier
{
    /// <summary>
    /// Classifies the supplied <see cref="ModelClass"/> into a canonical key and optional variant descriptor.
    /// </summary>
    /// <param name="model">Model metadata to inspect.</param>
    /// <returns>Classification result containing the normalized key and detected variant.</returns>
    LoraVariantClassification Classify(ModelClass model);
}
