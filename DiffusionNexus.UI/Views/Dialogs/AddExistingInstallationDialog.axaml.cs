using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

public partial class AddExistingInstallationDialog : Window
{
    public bool IsCancelled { get; private set; } = true;

    public AddExistingInstallationDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void OnConfirm(object sender, RoutedEventArgs e)
    {
        IsCancelled = false;
        Close();
    }

    public void OnCancel(object sender, RoutedEventArgs e)
    {
        IsCancelled = true;
        Close();
    }
}
