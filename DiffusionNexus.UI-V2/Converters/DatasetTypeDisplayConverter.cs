using Avalonia.Data.Converters;
using DiffusionNexus.Domain.Enums;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Converter to display DatasetType enum values as user-friendly strings.
/// </summary>
public class DatasetTypeDisplayConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for XAML usage.
    /// </summary>
    public static readonly DatasetTypeDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DatasetType type)
        {
            return type.GetDisplayName();
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
