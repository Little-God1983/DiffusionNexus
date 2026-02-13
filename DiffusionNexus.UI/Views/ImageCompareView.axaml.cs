using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class ImageCompareView : UserControl
{
    /// <summary>
    /// Horizontal scroll distance in pixels per click or wheel tick (~3 thumbnails at 98px each).
    /// </summary>
    private const double ScrollStep = 294;

    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _mouseHintTimer;
    private Border? _trayRoot;
    private ListBox? _filmstripListBox;
    private Border? _mouseHintOverlay;
    private ScrollViewer? _filmstripScrollViewer;
    private bool _hasShownMouseHint;

    public ImageCompareView()
    {
        InitializeComponent();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            SetTrayOpen(false);
        };

        _mouseHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mouseHintTimer.Tick += OnMouseHintTimerTick;

        _trayRoot = this.FindControl<Border>("TrayRoot");

        if (_trayRoot is not null)
        {
            _trayRoot.PointerEntered += OnTrayPointerEntered;
            _trayRoot.PointerExited += OnTrayPointerExited;
        }

        _filmstripListBox = this.FindControl<ListBox>("FilmstripListBox");
        if (_filmstripListBox is not null)
        {
            _filmstripListBox.AddHandler(PointerPressedEvent, OnFilmstripListBoxPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        }

        _mouseHintOverlay = this.FindControl<Border>("MouseHintOverlay");

        // Forward mouse wheel on the filmstrip area to horizontal scroll
        var filmstripBorder = this.FindControl<Grid>("FilmstripBorder");
        if (filmstripBorder is not null)
        {
            filmstripBorder.AddHandler(PointerWheelChangedEvent, OnFilmstripPointerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Reset so the hint shows again the next time the tray opens
        _hasShownMouseHint = false;

        // If the tray is already open (pinned), show the hint now; otherwise hide until opened
        if (DataContext is ImageCompareViewModel { IsTrayOpen: true })
        {
            ShowMouseHint();
        }
        else if (_mouseHintOverlay is not null)
        {
            _mouseHintOverlay.IsVisible = false;
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _mouseHintTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void ShowMouseHint()
    {
        if (_mouseHintOverlay is null || _hasShownMouseHint) return;

        _hasShownMouseHint = true;
        _mouseHintOverlay.Opacity = 0.9;
        _mouseHintOverlay.IsVisible = true;
        _mouseHintTimer.Stop();
        _mouseHintTimer.Start();
    }

    private void OnMouseHintTimerTick(object? sender, EventArgs e)
    {
        _mouseHintTimer.Stop();

        if (_mouseHintOverlay is null) return;

        // Animate opacity to 0, then hide
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(500),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.9) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0) } },
            }
        };

        animation.RunAsync(_mouseHintOverlay).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_mouseHintOverlay is not null)
                {
                    _mouseHintOverlay.IsVisible = false;
                }
            });
        });
    }

    /// <summary>
    /// Converts vertical mouse wheel events to horizontal scroll on the filmstrip.
    /// </summary>
    private void OnFilmstripPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        // Resolve the ListBox's built-in ScrollViewer (child of the ListBox template)
        if (_filmstripScrollViewer is null && _filmstripListBox is not null)
        {
            _filmstripScrollViewer = _filmstripListBox.Scroll as ScrollViewer;
        }
        if (_filmstripScrollViewer is null) return;

        // Convert vertical wheel delta to horizontal scroll
        var scrollAmount = -e.Delta.Y * ScrollStep;
        _filmstripScrollViewer.Offset = _filmstripScrollViewer.Offset.WithX(
            Math.Max(0, _filmstripScrollViewer.Offset.X + scrollAmount));
        e.Handled = true;
    }

    /// <summary>
    /// Scrolls the filmstrip left by one page of thumbnails.
    /// </summary>
    private void OnScrollLeftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScrollFilmstrip(-ScrollStep);
    }

    /// <summary>
    /// Scrolls the filmstrip right by one page of thumbnails.
    /// </summary>
    private void OnScrollRightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScrollFilmstrip(ScrollStep);
    }

    private void ScrollFilmstrip(double delta)
    {
        if (_filmstripScrollViewer is null && _filmstripListBox is not null)
        {
            _filmstripScrollViewer = _filmstripListBox.Scroll as ScrollViewer;
        }
        if (_filmstripScrollViewer is null) return;

        var newX = Math.Clamp(
            _filmstripScrollViewer.Offset.X + delta,
            0,
            _filmstripScrollViewer.Extent.Width - _filmstripScrollViewer.Viewport.Width);
        _filmstripScrollViewer.Offset = _filmstripScrollViewer.Offset.WithX(newX);
    }

    private void OnTrayPointerEntered(object? sender, PointerEventArgs e)
    {
        _collapseTimer.Stop();
        SetTrayOpen(true);
    }

    private void OnTrayPointerExited(object? sender, PointerEventArgs e)
    {
        // Only start collapse if pointer truly left the tray area
        if (_trayRoot is null)
        {
            return;
        }

        var position = e.GetPosition(_trayRoot);
        var bounds = new Rect(0, 0, _trayRoot.Bounds.Width, _trayRoot.Bounds.Height);

        if (!bounds.Contains(position))
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }
    }

    private void SetTrayOpen(bool isOpen)
    {
        if (DataContext is ImageCompareViewModel viewModel && !viewModel.IsPinned)
        {
            viewModel.IsTrayOpen = isOpen;
        }

        // Show the mouse hint the first time the tray opens
        if (isOpen)
        {
            ShowMouseHint();
        }
    }

    private void OnFilmstripListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control sourceControl &&
            sourceControl.DataContext is ImageCompareItem item &&
            DataContext is ImageCompareViewModel viewModel)
        {
            var properties = e.GetCurrentPoint(sourceControl).Properties;

            if (properties.IsLeftButtonPressed)
            {
                viewModel.AssignLeftImage(item);
                e.Handled = true;
            }
            else if (properties.IsRightButtonPressed)
            {
                viewModel.AssignRightImage(item);
                e.Handled = true;
            }
        }
    }
}
