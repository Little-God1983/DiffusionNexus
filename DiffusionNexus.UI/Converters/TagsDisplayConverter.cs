using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace DiffusionNexus.UI.Converters
{
    public class TagsDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // TODO: Implement
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
