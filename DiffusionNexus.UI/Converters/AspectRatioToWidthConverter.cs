using Avalonia.Data.Converters;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Computes tile width from aspect ratio and tile height.
/// Values[0] = aspect ratio (double), Values[1] = tile height (double).
/// Returns <c>tileHeight * aspectRatio</c>.
/// </summary>
public class AspectRatioToWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2
            && values[0] is double aspectRatio
            && values[1] is double tileHeight
            && aspectRatio > 0
            && tileHeight > 0)
        {
            return tileHeight * aspectRatio;
        }

        // Fallback: square tile when aspect ratio is unknown
        if (values.Count >= 2 && values[1] is double fallbackHeight)
        {
            return fallbackHeight;
        }

        return 220.0;
    }
}
