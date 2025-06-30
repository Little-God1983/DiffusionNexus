using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class LoraSortMainSettingsControl : UserControl
    {
        public LoraSortMainSettingsControl()
        {
            InitializeComponent();
            DataContext = new LoraSortMainSettingsViewModel(new SettingsService(), LogEventService.Instance);
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is LoraSortMainSettingsViewModel vm && VisualRoot is Window window)
            {
                vm.SetWindow(window);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}