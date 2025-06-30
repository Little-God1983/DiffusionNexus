using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LoraSortMainSettingsControl : UserControl
    {
        public LoraSortMainSettingsControl()
        {
            InitializeComponent();
            DataContext = new LoraSortMainSettingsViewModel();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is LoraSortMainSettingsViewModel vm && VisualRoot is Window window)
            {
                vm.SetWindow(window);
                if (window.DataContext is MainWindowViewModel mw)
                    vm.SetMainWindowViewModel(mw);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}