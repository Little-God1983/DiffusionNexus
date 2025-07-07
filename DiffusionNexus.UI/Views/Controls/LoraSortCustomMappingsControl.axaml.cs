using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LoraSortCustomMappingsControl : UserControl
    {
        public LoraSortCustomMappingsControl()
        {
            InitializeComponent();
            this.AttachedToVisualTree += (_, _) => HookVm();
            this.DataContextChanged += (_, _) => HookVm();
        }

        private void HookVm()
        {
            if (DataContext is LoraSortCustomMappingsViewModel vm && VisualRoot is Window window)
            {
                vm.SetWindow(window);
                vm.DialogService = new DialogService(window);
            }
        }

        private void DisableOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is LoraSortCustomMappingsViewModel vm)
                vm.NotifyDisabledInteraction();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
