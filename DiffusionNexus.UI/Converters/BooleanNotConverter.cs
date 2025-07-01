using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

public class BooleanNotConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return Avalonia.Data.BindingOperations.DoNothing;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
