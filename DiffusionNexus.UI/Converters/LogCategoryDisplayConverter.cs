using Avalonia.Data.Converters;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Renders a <see cref="LogCategory"/>? combo-box item. The <c>null</c> value represents
/// "no category filter" and is shown as "All Categories" so the user can explicitly pick
/// it from the dropdown — the placeholder text alone is invisible once any other category
/// has been selected (e.g. when launching a ComfyUI instance auto-filters to
/// <see cref="LogCategory.InstanceManagement"/>).
/// </summary>
public sealed class LogCategoryDisplayConverter : IValueConverter
{
    public static readonly LogCategoryDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null              => "All Categories",
            LogCategory enumValue => enumValue.ToString(),
            _                 => value.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
