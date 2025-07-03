using Avalonia.Data.Converters;
using Avalonia.Media;
using DiffusionNexus.Service.Classes;
using System;
using System.Globalization;

namespace DiffusionNexus.UI.Converters
{
    public class LogLevelToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is LogSeverity level)
            {
                return level switch
                {
                    LogSeverity.Info => Brushes.Blue,
                    LogSeverity.Success => Brushes.Green,
                    LogSeverity.Warning => Brushes.Gold,
                    LogSeverity.Error => Brushes.IndianRed,
                    LogSeverity.Debug => Brushes.DodgerBlue,
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
