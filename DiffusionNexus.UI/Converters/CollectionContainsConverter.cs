using Avalonia.Data.Converters;
using Avalonia.Data;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI.Converters
{
    public class CollectionContainsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is IEnumerable<string> collection && parameter is string item)
                return collection.Contains(item);
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // This converter is intended for one-way bindings only
            return BindingOperations.DoNothing;
        }
    }
}
