using Avalonia.Data.Converters;
using DiffusionNexus.UI.ViewModels.Tabs;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Converter to display <see cref="UpscalePromptMode"/>, <see cref="UpscaleSaveMode"/>,
/// and <see cref="CaptionSaveMode"/> enum values as user-friendly strings.
/// </summary>
public class UpscaleEnumDisplayConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for XAML usage.
    /// </summary>
    public static readonly UpscaleEnumDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            UpscalePromptMode promptMode => promptMode.GetDisplayName(),
            UpscaleSaveMode saveMode => saveMode.GetDisplayName(),
            CaptionSaveMode captionSaveMode => captionSaveMode.GetDisplayName(),
            _ => value?.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
