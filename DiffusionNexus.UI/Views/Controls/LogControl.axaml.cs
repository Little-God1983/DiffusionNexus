using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LogControl : UserControl
    {
        private ScrollViewer? _scroll;

        public LogControl()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
            this.DataContextChanged += (_, _) => HookDataContext();
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _scroll = this.FindControl<ScrollViewer>("LogScroll");
            HookDataContext();
        }

        private void HookDataContext()
        {
            if (DataContext is ViewModels.LogViewModel vm)
            {
                vm.Entries.CollectionChanged += (_, _) => _scroll?.ScrollToEnd();
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}