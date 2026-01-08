/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Enums;

/// <summary>
/// Diffusion model types used in the Service layer.
/// Maps to <see cref="ModelType"/> from the Domain layer.
/// </summary>
/// <remarks>
/// This enum is maintained for backward compatibility with existing code.
/// New code should prefer using <see cref="ModelType"/> directly.
/// </remarks>
public enum DiffusionTypes
{
    CHECKPOINT = ModelType.Checkpoint,
    LORA = ModelType.LORA,
    DORA = ModelType.DoRA,
    HYPERNETWORK = ModelType.Hypernetwork,
    CONTROLNET = ModelType.Controlnet,
    MOTION = ModelType.Motion,
    VAE = ModelType.VAE,
    WILDCARDS = ModelType.Wildcards,
    EMBEDDING = ModelType.Embedding,
    LYCORIS = ModelType.LoCon,
    POSES = ModelType.Poses,
    AESTHETICGRADIENT = ModelType.AestheticGradient,
    WORKFLOWS = ModelType.Workflows,
    TEXTUALINVERSION = ModelType.TextualInversion,
    UPSCALER = ModelType.Upscaler,
    DETECTION = ModelType.Detection,
    LOCON = ModelType.LoCon,
    OTHER = ModelType.Other,
    UNASSIGNED = ModelType.Unknown
}

/// <summary>
/// Extension methods for converting between DiffusionTypes and ModelType.
/// </summary>
public static class DiffusionTypesExtensions
{
    /// <summary>
    /// Converts a DiffusionTypes value to the equivalent ModelType.
    /// </summary>
    public static ModelType ToModelType(this DiffusionTypes diffusionType)
        => (ModelType)(int)diffusionType;

    /// <summary>
    /// Converts a ModelType value to the equivalent DiffusionTypes.
    /// </summary>
    public static DiffusionTypes ToDiffusionType(this ModelType modelType)
        => (DiffusionTypes)(int)modelType;
}
