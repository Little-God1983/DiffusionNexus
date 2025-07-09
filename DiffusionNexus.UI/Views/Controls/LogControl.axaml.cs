using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Collections.Specialized;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LogControl : UserControl
    {
        private ScrollViewer? _scroll;
        private bool _autoScroll = true;
        // Keep a reference so we can detach when the DataContext changes
        private INotifyCollectionChanged? _currentEntries;

        public LogControl()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
            this.DataContextChanged += (_, _) => HookDataContext();
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _scroll = this.FindControl<ScrollViewer>("LogScroll");
            if (_scroll != null)
                _scroll.ScrollChanged += OnScrollChanged;
            HookDataContext();
        }

        private void HookDataContext()
        {
            // 1) Detach from the previous collection (if any)
            if (_currentEntries is not null)
                _currentEntries.CollectionChanged -= OnEntriesChanged;

            _currentEntries = null;

            // 2) Attach to the new one (if it exists)
            if (DataContext is ViewModels.LogViewModel { Entries: { } entries })
            {
                _currentEntries = entries;
                _currentEntries.CollectionChanged += OnEntriesChanged;
            }
        }
        
        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Ensure UI operations are performed on the UI thread
            if (Dispatcher.UIThread.CheckAccess())
            {
                // Already on UI thread, safe to execute directly
                if (_autoScroll)
                    _scroll?.ScrollToEnd();
            }
            else
            {
                // Not on UI thread, dispatch to UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    if (_autoScroll)
                        _scroll?.ScrollToEnd();
                });
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_scroll == null) return;
            _autoScroll = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 1;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
