namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Utility methods for <see cref="BaseModelType"/>.
/// </summary>
public static class BaseModelTypeExtensions
{
    /// <summary>
    /// Parses a Civitai baseModel string to the corresponding BaseModelType.
    /// Works by convention: enum names are the Civitai strings with spaces and dots
    /// removed. No hardcoded mapping needed. Adding a new enum member is sufficient.
    /// </summary>
    public static BaseModelType ParseCivitai(string? civitaiBaseModel)
    {
        if (string.IsNullOrWhiteSpace(civitaiBaseModel))
            return BaseModelType.Unknown;

        var normalized = civitaiBaseModel
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal);

        return Enum.TryParse<BaseModelType>(normalized, ignoreCase: true, out var result)
            ? result
            : BaseModelType.Other;
    }
}
