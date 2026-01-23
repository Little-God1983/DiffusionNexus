using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable rating buttons control with Mark Ready, Mark Trash, and Clear Rating buttons.
/// Provides a consistent rating UI across the application.
/// </summary>
public partial class RatingButtonsControl : UserControl
{
    /// <summary>
    /// Defines the <see cref="Rating"/> property.
    /// </summary>
    public static readonly StyledProperty<ImageRatingStatus> RatingProperty =
        AvaloniaProperty.Register<RatingButtonsControl, ImageRatingStatus>(
            nameof(Rating), 
            ImageRatingStatus.Unrated,
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="ShowLabel"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> ShowLabelProperty =
        AvaloniaProperty.Register<RatingButtonsControl, bool>(nameof(ShowLabel), true);

    /// <summary>
    /// Defines the <see cref="IsApproved"/> property.
    /// </summary>
    public static readonly DirectProperty<RatingButtonsControl, bool> IsApprovedProperty =
        AvaloniaProperty.RegisterDirect<RatingButtonsControl, bool>(
            nameof(IsApproved),
            o => o.IsApproved);

    /// <summary>
    /// Defines the <see cref="IsRejected"/> property.
    /// </summary>
    public static readonly DirectProperty<RatingButtonsControl, bool> IsRejectedProperty =
        AvaloniaProperty.RegisterDirect<RatingButtonsControl, bool>(
            nameof(IsRejected),
            o => o.IsRejected);

    /// <summary>
    /// Defines the <see cref="IsUnrated"/> property.
    /// </summary>
    public static readonly DirectProperty<RatingButtonsControl, bool> IsUnratedProperty =
        AvaloniaProperty.RegisterDirect<RatingButtonsControl, bool>(
            nameof(IsUnrated),
            o => o.IsUnrated);

    /// <summary>
    /// Gets or sets the current rating status.
    /// </summary>
    public ImageRatingStatus Rating
    {
        get => GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    /// <summary>
    /// Gets or sets whether to show the "Rating:" label.
    /// </summary>
    public bool ShowLabel
    {
        get => GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    /// <summary>
    /// Gets whether the current rating is Approved.
    /// </summary>
    public bool IsApproved => Rating == ImageRatingStatus.Approved;

    /// <summary>
    /// Gets whether the current rating is Rejected.
    /// </summary>
    public bool IsRejected => Rating == ImageRatingStatus.Rejected;

    /// <summary>
    /// Gets whether the current rating is Unrated.
    /// </summary>
    public bool IsUnrated => Rating == ImageRatingStatus.Unrated;

    /// <summary>
    /// Event raised when the rating changes.
    /// </summary>
    public event EventHandler<ImageRatingStatus>? RatingChanged;

    public RatingButtonsControl()
    {
        InitializeComponent();
        
        this.AttachedToVisualTree += (s, e) => 
        {
            // Simple check to ensure resources are available
            if (Application.Current is null) return;
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RatingProperty)
        {
            // Notify computed properties by raising property changed for direct properties
            RaisePropertyChanged(IsApprovedProperty, !IsApproved, IsApproved);
            RaisePropertyChanged(IsRejectedProperty, !IsRejected, IsRejected);
            RaisePropertyChanged(IsUnratedProperty, !IsUnrated, IsUnrated);
        }
    }

    private void OnMarkReadyClick(object? sender, RoutedEventArgs e)
    {
        // Toggle: if already approved, clear; otherwise set to approved
        Rating = IsApproved ? ImageRatingStatus.Unrated : ImageRatingStatus.Approved;
        RatingChanged?.Invoke(this, Rating);
    }

    private void OnMarkTrashClick(object? sender, RoutedEventArgs e)
    {
        // Toggle: if already rejected, clear; otherwise set to rejected
        Rating = IsRejected ? ImageRatingStatus.Unrated : ImageRatingStatus.Rejected;
        RatingChanged?.Invoke(this, Rating);
    }

    private void OnClearRatingClick(object? sender, RoutedEventArgs e)
    {
        Rating = ImageRatingStatus.Unrated;
        RatingChanged?.Invoke(this, Rating);
    }
}
