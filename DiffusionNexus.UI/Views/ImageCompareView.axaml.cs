using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class ImageCompareView : UserControl
{
    private readonly DispatcherTimer _collapseTimer;
    private Border? _trayRoot;

    public ImageCompareView()
    {
        InitializeComponent();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            SetTrayOpen(false);
        };

        _trayRoot = this.FindControl<Border>("TrayRoot");

        if (_trayRoot is not null)
        {
            _trayRoot.PointerEntered += OnTrayPointerEntered;
            _trayRoot.PointerExited += OnTrayPointerExited;
        }
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
    }

    private void OnFilmstripItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control &&
            control.DataContext is ImageCompareItem item &&
            DataContext is ImageCompareViewModel viewModel)
        {
            var properties = e.GetCurrentPoint(control).Properties;

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
