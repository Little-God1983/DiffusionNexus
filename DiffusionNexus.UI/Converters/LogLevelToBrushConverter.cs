using Avalonia.Data.Converters;
using Avalonia.Media;
using DiffusionNexus.UI.Models;
using System;
using System.Globalization;

namespace DiffusionNexus.UI.Converters
{
    public class LogLevelToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is LogLevel level)
            {
                return level switch
                {
                    LogLevel.Success => Brushes.DarkGreen,
                    LogLevel.Warning => Brushes.Gold,
                    LogLevel.Error => Brushes.IndianRed,
                    _ => Brushes.Gray,
                };
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
