using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LogControl : UserControl
    {
        private ScrollViewer? _scroll;
        private bool _autoScroll = true;

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
            if (DataContext is ViewModels.LogViewModel vm)
            {
                vm.Entries.CollectionChanged += (_, _) =>
                {
                    if (_autoScroll)
                        _scroll?.ScrollToEnd();
                };
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