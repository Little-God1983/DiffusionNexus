using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog window displaying ComfyUI workloads in a tabbed table.
/// </summary>
public partial class WorkloadsDialog : Window
{
    public WorkloadsDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
