using Avalonia.Data.Converters;
using Avalonia.Media;

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

    #region Selection Converters

    private static readonly IBrush SelectionBlueBrush = new SolidColorBrush(Color.Parse("#2196F3"));
    private static readonly IBrush DefaultBorderBrush = new SolidColorBrush(Color.Parse("#444"));

    /// <summary>
    /// Converts IsSelected to border brush (blue if selected, default gray otherwise).
    /// </summary>
    public static readonly IValueConverter BoolToSelectionBorder =
        new FuncValueConverter<bool, IBrush>(b => b ? SelectionBlueBrush : DefaultBorderBrush);

    /// <summary>
    /// Converts IsSelected to border thickness (3 if selected, 2 otherwise).
    /// </summary>
    public static readonly IValueConverter BoolToSelectionThickness =
        new FuncValueConverter<bool, Avalonia.Thickness>(b => b ? new Avalonia.Thickness(3) : new Avalonia.Thickness(2));

    #endregion

    #region Rating Converters

    private static readonly IBrush ApprovedGreen = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush RejectedRed = new SolidColorBrush(Color.Parse("#D9534F"));
    private static readonly IBrush NeutralGray = new SolidColorBrush(Color.Parse("#80000000"));
    private static readonly IBrush TransparentBrush = new SolidColorBrush(Color.Parse("#444"));
    private static readonly IBrush WhiteBrush = Brushes.White;

    /// <summary>
    /// Converts IsApproved to border brush (green if approved, default gray otherwise).
    /// </summary>
    public static readonly IValueConverter BoolToApprovedBorder =
        new FuncValueConverter<bool, IBrush>(b => b ? ApprovedGreen : TransparentBrush);

    /// <summary>
    /// Converts IsApproved to background for badge (green if approved, red if rejected via MultiBinding).
    /// For simple bool binding, returns green if true.
    /// </summary>
    public static readonly IValueConverter BoolToApprovedBackground =
        new FuncValueConverter<bool, IBrush>(b => b ? ApprovedGreen : RejectedRed);

    /// <summary>
    /// Converts IsApproved to text for badge.
    /// </summary>
    public static readonly IValueConverter BoolToApprovedText =
        new FuncValueConverter<bool, string>(b => b ? "Ready" : "Failed");

    /// <summary>
    /// Converts IsApproved to button background (highlighted when active).
    /// </summary>
    public static readonly IValueConverter BoolToApprovedButtonBackground =
        new FuncValueConverter<bool, IBrush>(b => b ? ApprovedGreen : NeutralGray);

    /// <summary>
    /// Converts IsApproved to button foreground.
    /// </summary>
    public static readonly IValueConverter BoolToApprovedButtonForeground =
        new FuncValueConverter<bool, IBrush>(b => WhiteBrush);

    /// <summary>
    /// Converts IsRejected to button background (highlighted when active).
    /// </summary>
    public static readonly IValueConverter BoolToRejectedButtonBackground =
        new FuncValueConverter<bool, IBrush>(b => b ? RejectedRed : NeutralGray);

    /// <summary>
    /// Converts IsRejected to button foreground.
    /// </summary>
    public static readonly IValueConverter BoolToRejectedButtonForeground =
        new FuncValueConverter<bool, IBrush>(b => WhiteBrush);

    #endregion
}
