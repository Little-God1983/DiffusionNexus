using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is SettingsViewModel vm && VisualRoot is Window window)
            {
                vm.SetWindow(window);
            }
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
