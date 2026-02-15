using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Code-behind for the Installer Manager view.
/// Handles auto-scroll on console output and tray hover/collapse behavior.
/// </summary>
public partial class InstallerManagerView : UserControl
{
    private Border? _trayRoot;
    private Border? _trayHandle;
    private ListBox? _consoleListBox;
    private readonly DispatcherTimer _collapseTimer;

    /// <summary>
    /// Tracks the currently observed ConsoleLines collection so we can
    /// unsubscribe when the selected tab (and thus the ItemsSource) changes.
    /// </summary>
    private INotifyCollectionChanged? _currentConsoleCollection;

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
        WireControls();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        WireSelectedTabChanged();
    }

    private void WireControls()
    {
        _trayRoot = this.FindControl<Border>("TrayRoot");
        _trayHandle = this.FindControl<Border>("TrayHandle");
        _consoleListBox = this.FindControl<ListBox>("ConsoleListBox");

        // Tray hover events
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

        WireSelectedTabChanged();
    }

    /// <summary>
    /// Watches for SelectedTab changes so we rebind auto-scroll to the
    /// correct ConsoleLines collection.
    /// </summary>
    private void WireSelectedTabChanged()
    {
        if (DataContext is InstallerManagerViewModel vm)
        {
            vm.ConsoleTray.PropertyChanged -= OnConsoleTrayPropertyChanged;
            vm.ConsoleTray.PropertyChanged += OnConsoleTrayPropertyChanged;
            RebindAutoScroll(vm.ConsoleTray.SelectedTab);
        }
    }

    private void OnConsoleTrayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessConsoleTrayViewModel.SelectedTab)
            && DataContext is InstallerManagerViewModel vm)
        {
            RebindAutoScroll(vm.ConsoleTray.SelectedTab);
        }
    }

    /// <summary>
    /// Subscribes to the ConsoleLines collection of the given card for auto-scroll.
    /// </summary>
    private void RebindAutoScroll(InstallerPackageCardViewModel? card)
    {
        // Unsubscribe from previous collection
        if (_currentConsoleCollection is not null)
        {
            _currentConsoleCollection.CollectionChanged -= OnConsoleLinesChanged;
            _currentConsoleCollection = null;
        }

        if (card is not null)
        {
            _currentConsoleCollection = card.ConsoleLines;
            _currentConsoleCollection.CollectionChanged += OnConsoleLinesChanged;
        }
    }

    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _consoleListBox is not null)
        {
            // Post so the ListBox has time to add the container before we scroll
            Dispatcher.UIThread.Post(() =>
            {
                if (_consoleListBox.ItemCount > 0)
                {
                    _consoleListBox.ScrollIntoView(_consoleListBox.ItemCount - 1);
                }
            }, DispatcherPriority.Background);
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
