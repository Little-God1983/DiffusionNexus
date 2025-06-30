using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace DiffusionNexus.UI.Converters
{
    public class NullToAllConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value == null ? "All" : value.ToString();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
