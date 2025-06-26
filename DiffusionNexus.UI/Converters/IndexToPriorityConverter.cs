using Avalonia.Data.Converters;
using System;
using System.Globalization;
using System.Collections.Generic;

namespace DiffusionNexus.UI.Converters
{
    public class IndexToPriorityConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 2 || values[1] is not IEnumerable<object?> collection)
                return null;

            var item = values[0];
            var index = 0;
            foreach (var obj in collection)
            {
                if (Equals(obj, item))
                    return (index + 1).ToString();
                index++;
            }
            return null;
        }
    }
}
