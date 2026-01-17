using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Math helpers for XAML bindings.
/// </summary>
public static class MathConverters
{
    /// <summary>
    /// Multiplies numeric binding values together.
    /// </summary>
    public static readonly IMultiValueConverter Multiply = new MultiplyMultiValueConverter();

    private sealed class MultiplyMultiValueConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count == 0)
            {
                return 0d;
            }

            double result = 1d;

            foreach (var value in values)
            {
                if (value is null)
                {
                    return 0d;
                }

                if (value is double d)
                {
                    result *= d;
                }
                else if (value is float f)
                {
                    result *= f;
                }
                else if (value is int i)
                {
                    result *= i;
                }
                else if (value is decimal dec)
                {
                    result *= (double)dec;
                }
                else
                {
                    return 0d;
                }
            }

            return result;
        }

        public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
