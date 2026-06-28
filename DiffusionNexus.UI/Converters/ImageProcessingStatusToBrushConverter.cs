using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DiffusionNexus.UI.ViewModels.Controls;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Maps an <see cref="ImageProcessingStatus"/> to the outline brush of a status tile:
/// Pending → transparent, Processing → orange, Done → green, Failed → red. Mirrors (and extends with a
/// real error colour) the Batch Upscale strip's hard-coded #FF9800 / #4CAF50 styling.
/// </summary>
public sealed class ImageProcessingStatusToBrushConverter : IValueConverter
{
    public static readonly ImageProcessingStatusToBrushConverter Instance = new();

    private static readonly IBrush Processing = new SolidColorBrush(Color.Parse("#FF9800")); // orange
    private static readonly IBrush Done = new SolidColorBrush(Color.Parse("#4CAF50"));       // green
    private static readonly IBrush Failed = new SolidColorBrush(Color.Parse("#F44336"));     // red

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ImageProcessingStatus.Processing => Processing,
        ImageProcessingStatus.Done => Done,
        ImageProcessingStatus.Failed => Failed,
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
