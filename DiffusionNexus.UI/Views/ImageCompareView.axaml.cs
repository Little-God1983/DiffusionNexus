using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class ImageCompareView : UserControl
{
    private readonly DispatcherTimer _collapseTimer;

    public ImageCompareView()
    {
        InitializeComponent();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            SetTrayOpen(false);
        };

        var trayRoot = this.FindControl<Border>("TrayRoot");
        var trayHandle = this.FindControl<Border>("TrayHandle");

        if (trayRoot is not null)
        {
            trayRoot.PointerEntered += OnTrayPointerEntered;
            trayRoot.PointerExited += OnTrayPointerExited;
        }

        if (trayHandle is not null)
        {
            trayHandle.PointerEntered += OnTrayPointerEntered;
            trayHandle.PointerExited += OnTrayPointerExited;
        }
    }

    private void OnTrayPointerEntered(object? sender, PointerEventArgs e)
    {
        _collapseTimer.Stop();
        SetTrayOpen(true);
    }

    private void OnTrayPointerExited(object? sender, PointerEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void SetTrayOpen(bool isOpen)
    {
        if (DataContext is ImageCompareViewModel viewModel && !viewModel.IsPinned)
        {
            viewModel.IsTrayOpen = isOpen;
        }
    }
}
