using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

public class BooleanToBrushConverter : IValueConverter
{
    public IBrush TrueBrush { get; set; } = Brushes.LightGreen;
    public IBrush FalseBrush { get; set; } = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? TrueBrush : FalseBrush;
        return FalseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
