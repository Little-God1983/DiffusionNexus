using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void ContentArea_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && vm.IsMenuOpen)
                vm.IsMenuOpen = false;
        }
    }
}