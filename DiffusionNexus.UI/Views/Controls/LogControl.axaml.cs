using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LogControl : UserControl
    {
        private ScrollViewer? _scrollViewer;
        private bool _autoScroll = true;

        public LogControl()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _scrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is ViewModels.LogViewModel vm && VisualRoot is Window w)
            {
                vm.SetWindow(w);
                vm.VisibleEntries.CollectionChanged += (_, __) => ScrollIfNeeded();
            }
            if (_scrollViewer != null)
                _scrollViewer.ScrollChanged += ScrollViewerOnScrollChanged;
        }

        private void ScrollViewerOnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_scrollViewer == null) return;
            var maxOffset = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;
            _autoScroll = _scrollViewer.Offset.Y >= maxOffset - 1;
        }

        private void ScrollIfNeeded()
        {
            if (_autoScroll)
                _scrollViewer?.ScrollToEnd();
        }
    }
}