using Avalonia.Data.Converters;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Common boolean converters for XAML bindings.
/// </summary>
public static class BoolConverters
{
    /// <summary>
    /// Converts a boolean to opacity (true = 1.0, false = 0.5).
    /// </summary>
    public static readonly IValueConverter BoolToOpacity =
        new FuncValueConverter<bool, double>(b => b ? 1.0 : 0.5);

    /// <summary>
    /// Converts a boolean to opacity (true = 1.0, false = 0.3).
    /// </summary>
    public static readonly IValueConverter BoolToOpacityLow =
        new FuncValueConverter<bool, double>(b => b ? 1.0 : 0.3);
}
