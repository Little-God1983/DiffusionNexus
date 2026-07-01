using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Returns an accent brush when the bound value equals the <c>ConverterParameter</c>, otherwise a muted
/// brush. Used to highlight the selected option in a row of "toggle" buttons backed by an enum
/// (e.g. the Image-to-Image output aspect-ratio buttons).
/// </summary>
public sealed class EnumEqualsToBrushConverter : IValueConverter
{
    public static readonly EnumEqualsToBrushConverter Instance = new();

    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#0E639C"));   // accent blue
    private static readonly IBrush Unselected = new SolidColorBrush(Color.Parse("#3A3A3A")); // muted grey

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.Equals(parameter) ? Selected : Unselected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
