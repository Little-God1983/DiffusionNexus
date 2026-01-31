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
    private const double MinZoom = 0.1;
    private const double MaxZoom = 10.0;
    private const double ZoomStep = 0.1;

    public static readonly StyledProperty<string?> BeforeImagePathProperty =
        AvaloniaProperty.Register<ImageCompareControl, string?>(nameof(BeforeImagePath));

    public static readonly StyledProperty<string?> AfterImagePathProperty =
        AvaloniaProperty.Register<ImageCompareControl, string?>(nameof(AfterImagePath));

    public static readonly StyledProperty<double> SliderValueProperty =
        AvaloniaProperty.Register<ImageCompareControl, double>(nameof(SliderValue), 50d);

    public static readonly StyledProperty<CompareFitMode> FitModeProperty =
        AvaloniaProperty.Register<ImageCompareControl, CompareFitMode>(nameof(FitMode), CompareFitMode.Fit);

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<ImageCompareControl, double>(nameof(ZoomLevel), 1.0);

    private Control? _beforeContainer;
    private Control? _afterContainer;
    private Canvas? _overlayCanvas;
    private Border? _sliderLine;
    private Thumb? _sliderThumb;
    private Image? _beforeImage;
    private Image? _afterImage;

    private double _panX;
    private double _panY;
    private Point _lastPanPoint;
    private bool _isPanning;
    private bool _isFitMode = true;

    /// <summary>
    /// Event raised when zoom level changes.
    /// </summary>
    public event EventHandler? ZoomChanged;

    public ImageCompareControl()
    {
        InitializeComponent();

        _beforeContainer = this.FindControl<Control>("BeforeImageContainer");
        _afterContainer = this.FindControl<Control>("AfterImageContainer");
        _overlayCanvas = this.FindControl<Canvas>("OverlayCanvas");
        _sliderLine = this.FindControl<Border>("SliderLine");
        _sliderThumb = this.FindControl<Thumb>("SliderThumb");
        _beforeImage = this.FindControl<Image>("BeforeImageControl");
        _afterImage = this.FindControl<Image>("AfterImageControl");

        if (_overlayCanvas is not null)
        {
            _overlayCanvas.PointerPressed += OnOverlayPointerPressed;
            _overlayCanvas.PointerMoved += OnOverlayPointerMoved;
            _overlayCanvas.PointerReleased += OnOverlayPointerReleased;
            _overlayCanvas.PointerWheelChanged += OnOverlayPointerWheelChanged;
        }

        if (_sliderThumb is not null)
        {
            _sliderThumb.DragDelta += OnSliderThumbDragDelta;
        }

        PropertyChanged += OnControlPropertyChanged;

        // Update visuals when layout is complete
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SliderValueProperty || e.Property == BoundsProperty || e.Property == ZoomLevelProperty)
        {
            UpdateVisuals();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateVisuals();
    }

    public string? BeforeImagePath
    {
        get => GetValue(BeforeImagePathProperty);
        set => SetValue(BeforeImagePathProperty, value);
    }

    public string? AfterImagePath
    {
        get => GetValue(AfterImagePathProperty);
        set => SetValue(AfterImagePathProperty, value);
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

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set
        {
            var clamped = Math.Clamp(value, MinZoom, MaxZoom);
            SetValue(ZoomLevelProperty, clamped);
            _isFitMode = false;
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets the zoom level as a percentage (10-1000).
    /// </summary>
    public int ZoomPercentage => (int)Math.Round(ZoomLevel * 100);

    /// <summary>
    /// Gets or sets whether fit mode is active.
    /// </summary>
    public bool IsFitMode
    {
        get => _isFitMode;
        set
        {
            _isFitMode = value;
            if (value)
            {
                _panX = 0;
                _panY = 0;
            }
            UpdateVisuals();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_overlayCanvas is null)
        {
            return;
        }

        var props = e.GetCurrentPoint(_overlayCanvas).Properties;

        // Middle mouse button for panning
        if (props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPanPoint = e.GetPosition(_overlayCanvas);
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(_overlayCanvas);
        SetSliderFromPosition(position.X);
        e.Handled = true;
    }

    private void OnOverlayPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_overlayCanvas is null || !_isPanning)
        {
            return;
        }

        var point = e.GetPosition(_overlayCanvas);
        var deltaX = point.X - _lastPanPoint.X;
        var deltaY = point.Y - _lastPanPoint.Y;

        Pan(deltaX, deltaY);
        _lastPanPoint = point;
        e.Handled = true;
    }

    private void OnOverlayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Handled = true;
        }
    }

    private void OnOverlayPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Zoom with mouse wheel
        if (e.Delta.Y > 0)
        {
            ZoomIn();
        }
        else if (e.Delta.Y < 0)
        {
            ZoomOut();
        }

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
        if (_beforeContainer is null || _afterContainer is null || _overlayCanvas is null || _sliderLine is null || _sliderThumb is null)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Apply zoom transform to both image containers
        var zoomTransform = new TransformGroup();
        zoomTransform.Children.Add(new ScaleTransform(ZoomLevel, ZoomLevel));
        zoomTransform.Children.Add(new TranslateTransform(_panX, _panY));

        if (_beforeImage is not null)
        {
            _beforeImage.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _beforeImage.RenderTransform = zoomTransform;
        }

        if (_afterImage is not null)
        {
            _afterImage.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _afterImage.RenderTransform = zoomTransform;
        }

        var sliderX = width * (SliderValue / 100d);
        
        var afterClipRect = new Rect(sliderX, 0, Math.Max(0, width - sliderX), height);
        _afterContainer.Clip = new RectangleGeometry(afterClipRect);

        var beforeClipRect = new Rect(0, 0, sliderX, height);
        _beforeContainer.Clip = new RectangleGeometry(beforeClipRect);

        _sliderLine.Height = height;
        Canvas.SetLeft(_sliderLine, sliderX - (_sliderLine.Width / 2));
        Canvas.SetTop(_sliderLine, 0);

        var thumbWidth = _sliderThumb.Bounds.Width > 0 ? _sliderThumb.Bounds.Width : _sliderThumb.Width;
        var thumbHeight = _sliderThumb.Bounds.Height > 0 ? _sliderThumb.Bounds.Height : _sliderThumb.Height;

        Canvas.SetLeft(_sliderThumb, sliderX - (thumbWidth / 2));
        Canvas.SetTop(_sliderThumb, (height - thumbHeight) / 2);
    }

    /// <summary>
    /// Increases the zoom level.
    /// </summary>
    public void ZoomIn() => ZoomLevel += ZoomStep;

    /// <summary>
    /// Decreases the zoom level.
    /// </summary>
    public void ZoomOut() => ZoomLevel -= ZoomStep;

    /// <summary>
    /// Sets the zoom level to fit the images within the control.
    /// </summary>
    public void ZoomToFit()
    {
        SetValue(ZoomLevelProperty, 1.0);
        _panX = 0;
        _panY = 0;
        _isFitMode = true;
        UpdateVisuals();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets the zoom level to 100% (actual size).
    /// </summary>
    public void ZoomToActual()
    {
        ZoomLevel = 1.0;
        _panX = 0;
        _panY = 0;
    }

    /// <summary>
    /// Pans the images by the specified delta values.
    /// </summary>
    /// <param name="deltaX">The delta value for the X axis.</param>
    /// <param name="deltaY">The delta value for the Y axis.</param>
    public void Pan(double deltaX, double deltaY)
    {
        if (_isFitMode) return;
        _panX += deltaX;
        _panY += deltaY;
        UpdateVisuals();
    }

    /// <summary>
    /// Resets zoom to default state.
    /// </summary>
    public void ResetZoom()
    {
        SetValue(ZoomLevelProperty, 1.0);
        _panX = 0;
        _panY = 0;
        _isFitMode = true;
        UpdateVisuals();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }
}
