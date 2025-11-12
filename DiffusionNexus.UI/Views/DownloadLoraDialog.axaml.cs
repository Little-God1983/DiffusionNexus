using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class DownloadLoraDialog : Window
{
    public DownloadLoraDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is DownloadLoraDialogViewModel vm)
        {
            vm.SetWindow(this);
            vm.DialogService ??= new DialogService(this);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is DownloadLoraDialogViewModel vm)
        {
            vm.OnWindowClosing();
        }
    }
}
