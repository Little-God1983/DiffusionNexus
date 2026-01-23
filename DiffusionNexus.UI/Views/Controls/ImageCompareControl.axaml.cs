using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using DiffusionNexus.UI.Controls;

namespace DiffusionNexus.UI.Views.Controls;

public partial class ImageCompareControl : UserControl
{
    public static readonly StyledProperty<string?> BeforeImageProperty =
        AvaloniaProperty.Register<ImageCompareControl, string?>(nameof(BeforeImage));

    public static readonly StyledProperty<string?> AfterImageProperty =
        AvaloniaProperty.Register<ImageCompareControl, string?>(nameof(AfterImage));

    public static readonly StyledProperty<double> SliderValueProperty =
        AvaloniaProperty.Register<ImageCompareControl, double>(nameof(SliderValue), 50d);

    public static readonly StyledProperty<CompareFitMode> FitModeProperty =
        AvaloniaProperty.Register<ImageCompareControl, CompareFitMode>(nameof(FitMode), CompareFitMode.Fit);

    private Image? _afterImage;
    private Canvas? _overlayCanvas;
    private Border? _sliderLine;
    private Thumb? _sliderThumb;

    public ImageCompareControl()
    {
        InitializeComponent();

        _afterImage = this.FindControl<Image>("AfterImage");
        _overlayCanvas = this.FindControl<Canvas>("OverlayCanvas");
        _sliderLine = this.FindControl<Border>("SliderLine");
        _sliderThumb = this.FindControl<Thumb>("SliderThumb");

        if (_overlayCanvas is not null)
        {
            _overlayCanvas.PointerPressed += OnOverlayPointerPressed;
        }

        if (_sliderThumb is not null)
        {
            _sliderThumb.DragDelta += OnSliderThumbDragDelta;
        }

        this.GetObservable(SliderValueProperty).Subscribe(_ => UpdateVisuals());
        this.GetObservable(BoundsProperty).Subscribe(_ => UpdateVisuals());
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
        set => SetValue(SliderValueProperty, Math.Clamp(value, 0d, 100d));
    }

    public CompareFitMode FitMode
    {
        get => GetValue(FitModeProperty);
        set => SetValue(FitModeProperty, value);
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_overlayCanvas is null)
        {
            return;
        }

        var position = e.GetPosition(_overlayCanvas);
        SetSliderFromPosition(position.X);
        e.Handled = true;
    }

    private void OnSliderThumbDragDelta(object? sender, VectorEventArgs e)
    {
        var width = Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var sliderX = width * (SliderValue / 100d);
        var updatedX = sliderX + e.Vector.X;
        SliderValue = updatedX / width * 100d;
    }

    private void SetSliderFromPosition(double x)
    {
        var width = Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        SliderValue = x / width * 100d;
    }

    private void UpdateVisuals()
    {
        if (_afterImage is null || _overlayCanvas is null || _sliderLine is null || _sliderThumb is null)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var sliderX = width * (SliderValue / 100d);
        var clipRect = new Rect(sliderX, 0, Math.Max(0, width - sliderX), height);
        _afterImage.Clip = new RectangleGeometry(clipRect);

        _sliderLine.Height = height;
        Canvas.SetLeft(_sliderLine, sliderX - (_sliderLine.Width / 2));
        Canvas.SetTop(_sliderLine, 0);

        var thumbWidth = _sliderThumb.Bounds.Width > 0 ? _sliderThumb.Bounds.Width : _sliderThumb.Width;
        var thumbHeight = _sliderThumb.Bounds.Height > 0 ? _sliderThumb.Bounds.Height : _sliderThumb.Height;

        Canvas.SetLeft(_sliderThumb, sliderX - (thumbWidth / 2));
        Canvas.SetTop(_sliderThumb, (height - thumbHeight) / 2);
    }
}
