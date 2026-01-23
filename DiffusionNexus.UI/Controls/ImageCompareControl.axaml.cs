using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DiffusionNexus.UI.Controls;

public partial class ImageCompareControl : UserControl
{
    public static readonly StyledProperty<string?> BeforeImageProperty =
        AvaloniaProperty.Register<ImageCompareControl, string?>(nameof(BeforeImage));

    public static readonly StyledProperty<string?> AfterImageProperty =
        AvaloniaProperty.Register<ImageCompareControl, string?>(nameof(AfterImage));

    public static readonly StyledProperty<double> SliderValueProperty =
        AvaloniaProperty.Register<ImageCompareControl, double>(nameof(SliderValue), 0.5);

    public static readonly StyledProperty<ImageCompareFitMode> FitModeProperty =
        AvaloniaProperty.Register<ImageCompareControl, ImageCompareFitMode>(nameof(FitMode), ImageCompareFitMode.Fit);

    private RectangleGeometry? _beforeClip;
    private bool _isDragging;

    public ImageCompareControl()
    {
        InitializeComponent();
        UpdateFitMode();
        UpdateSliderVisuals();

        this.GetObservable(SliderValueProperty).Subscribe(_ => UpdateSliderVisuals());
        this.GetObservable(BoundsProperty).Subscribe(_ => UpdateSliderVisuals());
        this.GetObservable(FitModeProperty).Subscribe(_ => UpdateFitMode());
    }

    public string? BeforeImage
    {
        get => GetValue(BeforeImageProperty);
        set => SetValue(BeforeImageProperty, value);
    }

    public string? AfterImage
    {
        get => GetValue(AfterImageProperty);
        set => SetValue(AfterImageProperty, value);
    }

    public double SliderValue
    {
        get => GetValue(SliderValueProperty);
        set => SetValue(SliderValueProperty, value);
    }

    public ImageCompareFitMode FitMode
    {
        get => GetValue(FitModeProperty);
        set => SetValue(FitModeProperty, value);
    }

    private void UpdateFitMode()
    {
        var stretch = FitMode switch
        {
            ImageCompareFitMode.Fill => Stretch.UniformToFill,
            ImageCompareFitMode.OneToOne => Stretch.None,
            _ => Stretch.Uniform
        };

        var alignment = FitMode == ImageCompareFitMode.OneToOne ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        var verticalAlignment = FitMode == ImageCompareFitMode.OneToOne ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        BeforeImageControl.Stretch = stretch;
        BeforeImageControl.HorizontalAlignment = alignment;
        BeforeImageControl.VerticalAlignment = verticalAlignment;

        AfterImageControl.Stretch = stretch;
        AfterImageControl.HorizontalAlignment = alignment;
        AfterImageControl.VerticalAlignment = verticalAlignment;
    }

    private void UpdateSliderVisuals()
    {
        if (SliderCanvas is null || InputLayer is null || BeforeImageControl is null)
        {
            return;
        }

        var width = InputLayer.Bounds.Width;
        var height = InputLayer.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(SliderValue, 0, 1);
        var x = clamped * width;

        if (_beforeClip is null)
        {
            _beforeClip = new RectangleGeometry();
            BeforeImageControl.Clip = _beforeClip;
        }

        _beforeClip.Rect = new Rect(0, 0, x, height);

        SliderLine.Height = height;
        Canvas.SetLeft(SliderLine, x - (SliderLine.Width / 2));
        Canvas.SetTop(SliderLine, 0);

        Canvas.SetLeft(SliderThumb, x - (SliderThumb.Width / 2));
        Canvas.SetTop(SliderThumb, (height - SliderThumb.Height) / 2);
    }

    private void UpdateSliderFromPointer(PointerEventArgs e)
    {
        if (InputLayer is null)
        {
            return;
        }

        var point = e.GetPosition(InputLayer);
        var width = InputLayer.Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        SliderValue = Math.Clamp(point.X / width, 0, 1);
    }

    private void OnInputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDragging = true;
        InputLayer?.CapturePointer(e.Pointer);
        UpdateSliderFromPointer(e);
    }

    private void OnInputPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        UpdateSliderFromPointer(e);
    }

    private void OnInputPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        InputLayer?.ReleasePointerCapture(e.Pointer);
    }

    private void OnInputPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDragging = false;
    }
}
