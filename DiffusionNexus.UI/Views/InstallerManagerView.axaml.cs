using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Code-behind for the Installer Manager view.
/// Handles tray hover/collapse behavior matching the Image Comparer pattern.
/// </summary>
public partial class InstallerManagerView : UserControl
{
    private Border? _trayRoot;
    private Border? _trayHandle;
    private readonly DispatcherTimer _collapseTimer;

    public InstallerManagerView()
    {
        InitializeComponent();

        _collapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            SetTrayOpen(false);
        };
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireTrayEvents();
    }

    private void WireTrayEvents()
    {
        _trayRoot = this.FindControl<Border>("TrayRoot");
        _trayHandle = this.FindControl<Border>("TrayHandle");

        if (_trayHandle is not null)
        {
            _trayHandle.PointerEntered -= OnTrayPointerEntered;
            _trayHandle.PointerEntered += OnTrayPointerEntered;
        }

        if (_trayRoot is not null)
        {
            _trayRoot.PointerEntered -= OnTrayPointerEntered;
            _trayRoot.PointerEntered += OnTrayPointerEntered;
            _trayRoot.PointerExited -= OnTrayPointerExited;
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
        if (_trayRoot is null) return;

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
        if (DataContext is InstallerManagerViewModel vm && !vm.ConsoleTray.IsPinned)
        {
            vm.ConsoleTray.IsTrayOpen = isOpen;
        }
    }
}
