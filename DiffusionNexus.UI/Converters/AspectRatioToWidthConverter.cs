using Avalonia.Data.Converters;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Computes tile width from aspect ratio, tile height, and layout mode.
/// Values[0] = aspect ratio (double), Values[1] = tile height (double),
/// Values[2] = isShowcaseLayout (bool, optional — defaults to true).
/// Showcase mode returns <c>tileHeight * aspectRatio</c>; Grid mode returns <c>tileHeight</c> (square).
/// </summary>
public class AspectRatioToWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isShowcase = values.Count >= 3 && values[2] is bool mode ? mode : true;

        if (values.Count >= 2
            && values[0] is double aspectRatio
            && values[1] is double tileHeight
            && aspectRatio > 0
            && tileHeight > 0)
        {
            return isShowcase ? tileHeight * aspectRatio : tileHeight;
        }

        // Fallback: square tile when aspect ratio is unknown
        if (values.Count >= 2 && values[1] is double fallbackHeight)
        {
            return fallbackHeight;
        }

        return 220.0;
    }
}
