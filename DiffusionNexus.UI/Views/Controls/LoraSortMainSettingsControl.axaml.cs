using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LoraSortMainSettingsControl : UserControl
    {
        public LoraSortMainSettingsControl()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is LoraSortMainSettingsViewModel vm && VisualRoot is Window window)
            {
                vm.SetWindow(window);
                if (window.DataContext is MainWindowViewModel mw)
                    vm.SetMainWindowViewModel(mw);

                vm.DialogService = new DialogService(window);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}