using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DiffusionNexus.UI.Controls;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Converts <see cref="CompareFitMode"/> values to Avalonia <see cref="Stretch"/> options.
/// </summary>
public class CompareFitModeToStretchConverter : IValueConverter
{
    public static readonly CompareFitModeToStretchConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CompareFitMode mode)
        {
            return Stretch.Uniform;
        }

        return mode switch
        {
            CompareFitMode.Fill => Stretch.UniformToFill,
            CompareFitMode.OneToOne => Stretch.None,
            _ => Stretch.Uniform
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts <see cref="CompareFitMode"/> values to user-facing labels.
/// </summary>
public class CompareFitModeDisplayConverter : IValueConverter
{
    public static readonly CompareFitModeDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CompareFitMode mode)
        {
            return "Fit";
        }

        return mode switch
        {
            CompareFitMode.Fill => "Fill",
            CompareFitMode.OneToOne => "1:1",
            _ => "Fit"
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
