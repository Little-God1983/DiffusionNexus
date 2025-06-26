using Avalonia.Data.Converters;
using System;
using System.Globalization;
using System.Collections.Generic;

namespace DiffusionNexus.UI.Converters
{
    public class TagsDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null) return string.Empty;
            if (value is IEnumerable<string> tags)
                return string.Join(", ", tags);
            return value.ToString();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
