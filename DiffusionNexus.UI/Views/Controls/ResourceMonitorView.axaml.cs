using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable GPU VRAM + system RAM monitor widget. Polls its <see cref="ResourceMonitorViewModel"/>
/// on a low-frequency (2s) timer while it is attached to the visual tree, and stops when hidden so
/// it never samples in the background.
/// </summary>
public partial class ResourceMonitorView : UserControl
{
    private DispatcherTimer? _timer;

    public ResourceMonitorView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        TryRefresh();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e) => TryRefresh();

    private void TryRefresh()
    {
        if (DataContext is ResourceMonitorViewModel vm && vm.RefreshCommand.CanExecute(null))
            vm.RefreshCommand.Execute(null);
    }
}
