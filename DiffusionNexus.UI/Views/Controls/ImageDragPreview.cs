using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// A floating, click-through thumbnail that follows the cursor during a native file drag-out, with a
/// count badge when more than one item is dragged. Windows-only (uses Win32 to track the cursor and
/// make the window input-transparent); a no-op elsewhere. Call <see cref="Show"/> before starting the
/// drag and <see cref="Hide"/> when it finishes.
/// </summary>
public sealed class ImageDragPreview
{
    private const double PreviewSize = 100.0;
    private const int CursorOffset = 16;

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TRANSPARENT = 0x00000020;
    private const nint WS_EX_LAYERED = 0x00080000;

    private Window? _window;
    private DispatcherTimer? _cursorTimer;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Shows the preview at the cursor. No-op without a thumbnail or off Windows.</summary>
    public void Show(Bitmap? thumbnail, int count)
    {
        if (thumbnail is null || !OperatingSystem.IsWindows()) return;

        var grid = new Grid();
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#555")),
            BorderThickness = new Thickness(2),
            Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill },
        });

        if (count > 1)
        {
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2196F3")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, 8),
                Child = new TextBlock
                {
                    Text = count.ToString(),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    FontSize = 14,
                },
            });
        }

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            Width = PreviewSize,
            Height = PreviewSize,
            Opacity = 0.85,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = grid,
        };

        if (GetCursorPos(out var pt))
            window.Position = new PixelPoint(pt.X + CursorOffset, pt.Y + CursorOffset);

        _window = window;
        window.Show();

        SetWindowInputTransparent(window);

        // Poll cursor position to follow the pointer during the native drag.
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cursorTimer.Tick += (_, _) =>
        {
            if (_window is not null && GetCursorPos(out var cursor))
                _window.Position = new PixelPoint(cursor.X + CursorOffset, cursor.Y + CursorOffset);
        };
        _cursorTimer.Start();
    }

    /// <summary>Closes the preview and stops cursor tracking.</summary>
    public void Hide()
    {
        _cursorTimer?.Stop();
        _cursorTimer = null;
        _window?.Close();
        _window = null;
    }

    private static void SetWindowInputTransparent(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle is null) return;

        var hwnd = platformHandle.Handle;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
    }
}
