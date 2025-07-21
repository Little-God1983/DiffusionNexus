using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

public class EnumToBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return Avalonia.Data.BindingOperations.DoNothing;
        return value.ToString()!.Equals(parameter.ToString(), StringComparison.Ordinal);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}

