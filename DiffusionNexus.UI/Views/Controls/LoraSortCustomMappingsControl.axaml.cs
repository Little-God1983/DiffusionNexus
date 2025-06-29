using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LoraSortCustomMappingsControl : UserControl
    {
        public LoraSortCustomMappingsControl()
        {
            InitializeComponent();
            DataContext = new LoraSortCustomMappingsViewModel();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is LoraSortCustomMappingsViewModel vm && VisualRoot is Window window)
            {
                vm.SetWindow(window);
                vm.DialogService = new DialogService(window);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
