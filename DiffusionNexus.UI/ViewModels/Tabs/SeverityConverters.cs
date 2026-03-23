using Avalonia.Data.Converters;
using Avalonia.Media;
using DiffusionNexus.Domain.Enums;
using System.Globalization;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Converts <see cref="IssueSeverity"/> to a text icon symbol for the issue list.
/// </summary>
public sealed class SeverityIconConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for XAML static binding.
    /// </summary>
    public static readonly SeverityIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IssueSeverity severity)
        {
            return severity switch
            {
                IssueSeverity.Critical => "⛔",
                IssueSeverity.Warning => "⚠",
                IssueSeverity.Info => "ℹ",
                _ => "•"
            };
        }

        return "•";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts <see cref="IssueSeverity"/> to a boolean indicating whether it matches
/// a specific severity level. Used for conditional CSS class assignment in AXAML.
/// </summary>
public sealed class SeverityMatchConverter : IValueConverter
{
    private readonly IssueSeverity _target;

    private SeverityMatchConverter(IssueSeverity target) => _target = target;

    /// <summary>
    /// Returns true when severity is <see cref="IssueSeverity.Critical"/>.
    /// </summary>
    public static readonly SeverityMatchConverter CriticalInstance = new(IssueSeverity.Critical);

    /// <summary>
    /// Returns true when severity is <see cref="IssueSeverity.Warning"/>.
    /// </summary>
    public static readonly SeverityMatchConverter WarningInstance = new(IssueSeverity.Warning);

    /// <summary>
    /// Returns true when severity is <see cref="IssueSeverity.Info"/>.
    /// </summary>
    public static readonly SeverityMatchConverter InfoInstance = new(IssueSeverity.Info);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IssueSeverity severity)
        {
            return severity == _target;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts <see cref="IssueSeverity"/> to a background <see cref="IBrush"/>
/// for the severity badge in the detail panel.
/// </summary>
public sealed class SeverityBackgroundConverter : IValueConverter
{
    private static readonly IBrush CriticalBrush = new SolidColorBrush(Color.Parse("#CC3333"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#CC7700"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#555"));

    /// <summary>
    /// Singleton instance for XAML static binding.
    /// </summary>
    public static readonly SeverityBackgroundConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IssueSeverity severity)
        {
            return severity switch
            {
                IssueSeverity.Critical => CriticalBrush,
                IssueSeverity.Warning => WarningBrush,
                IssueSeverity.Info => InfoBrush,
                _ => DefaultBrush
            };
        }

        return DefaultBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
