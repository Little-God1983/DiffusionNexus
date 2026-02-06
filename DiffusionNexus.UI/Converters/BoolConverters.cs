using Avalonia.Data.Converters;
using Avalonia.Media;
using DiffusionNexus.UI.ViewModels;
using System.Globalization;

namespace DiffusionNexus.UI.Converters;

/// <summary>
/// Converts a boolean to accent brush for highlighting selected options.
/// </summary>
public class BoolToAccentBrushConverter : IValueConverter
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#667eea"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? AccentBrush : TransparentBrush;
        }
        return TransparentBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Common boolean converters for XAML bindings.
/// </summary>
public static class BoolConverters
{
    /// <summary>
    /// Negates a boolean value.
    /// </summary>
    public static readonly IValueConverter Not =
        new FuncValueConverter<bool, bool>(b => !b);

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
        new FuncValueConverter<bool, string>(b => b ? "Ready" : "Trash");

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

    #region Thumbnail Rating Badge Converters

    /// <summary>
    /// Converts ImageRatingStatus to badge visibility (visible only if rated).
    /// </summary>
    public static readonly IValueConverter RatingStatusToVisibility =
        new FuncValueConverter<ImageRatingStatus, bool>(status => status != ImageRatingStatus.Unrated);

    /// <summary>
    /// Converts ImageRatingStatus to badge background color (green for approved, red for rejected).
    /// </summary>
    public static readonly IValueConverter RatingStatusToBackground =
        new FuncValueConverter<ImageRatingStatus, IBrush>(status => status switch
        {
            ImageRatingStatus.Approved => ApprovedGreen,
            ImageRatingStatus.Rejected => RejectedRed,
            _ => Brushes.Transparent
        });

    /// <summary>
    /// Converts ImageRatingStatus to badge symbol (+ for approved, - for rejected).
    /// </summary>
    public static readonly IValueConverter RatingStatusToSymbol =
        new FuncValueConverter<ImageRatingStatus, string>(status => status switch
        {
            ImageRatingStatus.Approved => "+",
            ImageRatingStatus.Rejected => "-",
            _ => ""
        });

    #endregion

    #region Model Status Converters

    private static readonly IBrush ReadyGreenBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush NotReadyOrangeBrush = new SolidColorBrush(Color.Parse("#FF9800"));

    /// <summary>
    /// Converts IsModelReady boolean to status text ("Ready" or "Not Downloaded").
    /// </summary>
    public static readonly IValueConverter BoolToModelStatusText =
        new FuncValueConverter<bool, string>(isReady => isReady ? "Ready" : "Not Downloaded");

    /// <summary>
    /// Converts IsUpscalingModelReady boolean to status text for upscaling model.
    /// </summary>
    public static readonly IValueConverter BoolToUpscalingModelStatusText =
        new FuncValueConverter<bool, string>(isReady => isReady ? "4x-UltraSharp Ready" : "Not Downloaded");

    /// <summary>
    /// Converts IsModelReady boolean to status brush (green for ready, orange for not ready).
    /// </summary>
    public static readonly IValueConverter BoolToStatusBrush =
        new FuncValueConverter<bool, IBrush>(isReady => isReady ? ReadyGreenBrush : NotReadyOrangeBrush);

    /// <summary>
    /// Converts IsModelReady boolean to model path hint text.
    /// </summary>
    public static readonly IValueConverter BoolToModelPathText =
        new FuncValueConverter<bool, string>(isReady => 
            "Path: %LocalAppData%\\DiffusionNexus\\Models\\");

    /// <summary>
    /// Converts percentage (0-100) to width for custom progress bar.
    /// Assumes parent container is ~168px (200px panel - 16px padding - 16px border padding).
    /// </summary>
    public static readonly IValueConverter PercentageToWidth =
        new FuncValueConverter<int, double>(percentage => Math.Max(0, Math.Min(100, percentage)) * 1.68);

    #endregion

    #region Accent Border Converters

    private static readonly IBrush AccentGreenBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush InactiveBorderBrush = new SolidColorBrush(Color.Parse("#444"));

    /// <summary>
    /// Converts a boolean to accent border brush (green if true, gray otherwise).
    /// Used for highlighting selected options in dialogs.
    /// </summary>
    public static readonly IValueConverter ToAccentBorder =
        new FuncValueConverter<bool, IBrush>(b => b ? AccentGreenBrush : InactiveBorderBrush);

    #endregion

    #region Caption Editor Converters

    private static readonly IBrush UnsavedHighlightBrush = new SolidColorBrush(Color.Parse("#2D7D46")); // Green when unsaved
    private static readonly IBrush SavedDefaultBrush = new SolidColorBrush(Color.Parse("#80444444")); // Gray when saved

    /// <summary>
    /// Converts HasUnsavedChanges to save button background.
    /// Shows green when there are unsaved changes, gray otherwise.
    /// </summary>
    public static readonly IValueConverter BoolToUnsavedBackground =
        new FuncValueConverter<bool, IBrush>(hasUnsaved => hasUnsaved ? UnsavedHighlightBrush : SavedDefaultBrush);

    #endregion

    #region Backup Status Converters

    /// <summary>
    /// Converts IsAutoBackupConfigured to tooltip text.
    /// </summary>
    public static readonly IValueConverter BoolToBackupTooltip =
        new FuncValueConverter<bool, string>(isConfigured => 
            isConfigured ? "Automatic backup is configured. Click to view settings." 
                         : "Automatic backup is not configured. Click to set up backup.");

    #endregion

    #region Layer Converters

    private static readonly IBrush SelectedLayerBrush = new SolidColorBrush(Color.Parse("#3A5E9C"));
    private static readonly IBrush UnselectedLayerBrush = Brushes.Transparent;

    /// <summary>
    /// Converts layer selection state to background brush.
    /// </summary>
    public static readonly IValueConverter BoolToLayerBackground =
        new FuncValueConverter<bool, IBrush>(isSelected => isSelected ? SelectedLayerBrush : UnselectedLayerBrush);

    #endregion

    #region Crop Aspect Ratio Converters

    /// <summary>
    /// Converts CropAspectInverted to display text for the 16:9 button.
    /// </summary>
    public static readonly IValueConverter BoolToCropRatio16x9 =
        new FuncValueConverter<bool, string>(inverted => inverted ? "9:16" : "16:9");

    /// <summary>
    /// Converts CropAspectInverted to display text for the 4:3 button.
    /// </summary>
    public static readonly IValueConverter BoolToCropRatio4x3 =
        new FuncValueConverter<bool, string>(inverted => inverted ? "3:4" : "4:3");

    /// <summary>
    /// Converts CropAspectInverted to display text for the 5:4 button.
    /// </summary>
    public static readonly IValueConverter BoolToCropRatio5x4 =
        new FuncValueConverter<bool, string>(inverted => inverted ? "4:5" : "5:4");

    #endregion
}
