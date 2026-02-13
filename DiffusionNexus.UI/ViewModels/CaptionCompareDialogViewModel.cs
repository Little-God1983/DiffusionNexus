using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Converters used by the CaptionCompareDialog to highlight the selected caption panel.
/// </summary>
public static class CaptionCompareConverters
{
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush UnselectedBrush = Brushes.Transparent;

    /// <summary>
    /// Converts a bool to a highlight brush (green when selected, transparent otherwise).
    /// </summary>
    public static readonly IValueConverter BoolToHighlightBrush =
        new FuncValueConverter<bool, IBrush>(selected => selected ? SelectedBrush : UnselectedBrush);
}

/// <summary>
/// Result of the caption compare dialog indicating which caption the user chose.
/// </summary>
public class CaptionCompareResult
{
    /// <summary>Whether the user confirmed a selection (false = cancelled).</summary>
    public bool Confirmed { get; init; }

    /// <summary>The caption text the user chose to keep.</summary>
    public string? ChosenCaption { get; init; }

    /// <summary>Creates a cancelled result.</summary>
    public static CaptionCompareResult Cancelled() => new() { Confirmed = false };

    /// <summary>Creates a confirmed result with the chosen caption.</summary>
    public static CaptionCompareResult Chosen(string caption) => new() { Confirmed = true, ChosenCaption = caption };
}

/// <summary>
/// ViewModel for the caption compare dialog. Displays the current and newly generated
/// captions side by side, letting the user pick which one to keep.
/// </summary>
public partial class CaptionCompareDialogViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    [ObservableProperty]
    private Bitmap? _imagePreview;

    [ObservableProperty]
    private string _currentCaption = string.Empty;

    [ObservableProperty]
    private string _newCaption = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentSelected))]
    [NotifyPropertyChangedFor(nameof(IsNewSelected))]
    private bool _isLeftSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentSelected))]
    [NotifyPropertyChangedFor(nameof(IsNewSelected))]
    private bool _isRightSelected;

    /// <summary>Whether the "Current" (left) caption is highlighted.</summary>
    public bool IsCurrentSelected => IsLeftSelected;

    /// <summary>Whether the "New" (right) caption is highlighted.</summary>
    public bool IsNewSelected => IsRightSelected;

    /// <summary>The result set when the dialog closes.</summary>
    public CaptionCompareResult? Result { get; private set; }

    /// <summary>Command to select the current (left) caption.</summary>
    [RelayCommand]
    private void SelectCurrent()
    {
        IsLeftSelected = true;
        IsRightSelected = false;
    }

    /// <summary>Command to select the new (right) caption.</summary>
    [RelayCommand]
    private void SelectNew()
    {
        IsLeftSelected = false;
        IsRightSelected = true;
    }

    /// <summary>Command to confirm the selection and close.</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (IsLeftSelected)
            Result = CaptionCompareResult.Chosen(CurrentCaption);
        else if (IsRightSelected)
            Result = CaptionCompareResult.Chosen(NewCaption);
        else
            Result = CaptionCompareResult.Cancelled();

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Command to cancel without saving.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Result = CaptionCompareResult.Cancelled();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Initializes the dialog with image and caption data.
    /// </summary>
    public void Initialize(string imagePath, string currentCaption, string newCaption)
    {
        CurrentCaption = currentCaption;
        NewCaption = newCaption;
        ImagePreview = LoadPreview(imagePath);

        // Default-select "New" since the user just generated it
        SelectNew();
    }

    private static Bitmap? LoadPreview(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 400, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        ImagePreview?.Dispose();
        _disposed = true;
    }
}
