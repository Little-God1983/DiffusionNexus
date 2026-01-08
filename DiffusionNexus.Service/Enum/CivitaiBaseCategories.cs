/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Enums;

/// <summary>
/// Civitai model categories used in the Service layer.
/// Maps to <see cref="CivitaiCategory"/> from the Domain layer.
/// </summary>
/// <remarks>
/// This enum is maintained for backward compatibility with existing code.
/// New code should prefer using <see cref="CivitaiCategory"/> directly.
/// </remarks>
public enum CivitaiBaseCategories
{
    CHARACTER = CivitaiCategory.Character,
    STYLE = CivitaiCategory.Style,
    CELEBRITY = CivitaiCategory.Celebrity,
    CONCEPT = CivitaiCategory.Concept,
    CLOTHING = CivitaiCategory.Clothing,
    BASE_MODEL = CivitaiCategory.BaseModel,
    POSES = CivitaiCategory.Poses,
    BACKGROUND = CivitaiCategory.Background,
    TOOL = CivitaiCategory.Tool,
    BUILDINGS = CivitaiCategory.Buildings,
    VEHICLE = CivitaiCategory.Vehicle,
    OBJECTS = CivitaiCategory.Objects,
    ANIMAL = CivitaiCategory.Animal,
    ASSETS = CivitaiCategory.Assets,
    ACTION = CivitaiCategory.Action,
    UNKNOWN = CivitaiCategory.Unknown,
    UNASSIGNED = CivitaiCategory.Unknown
}

/// <summary>
/// Extension methods for converting between CivitaiBaseCategories and CivitaiCategory.
/// </summary>
public static class CivitaiBaseCategoriesExtensions
{
    /// <summary>
    /// Converts a CivitaiBaseCategories value to the equivalent CivitaiCategory.
    /// </summary>
    public static CivitaiCategory ToCivitaiCategory(this CivitaiBaseCategories category)
        => (CivitaiCategory)(int)category;

    /// <summary>
    /// Converts a CivitaiCategory value to the equivalent CivitaiBaseCategories.
    /// </summary>
    public static CivitaiBaseCategories ToCivitaiBaseCategories(this CivitaiCategory category)
        => (CivitaiBaseCategories)(int)category;
}
