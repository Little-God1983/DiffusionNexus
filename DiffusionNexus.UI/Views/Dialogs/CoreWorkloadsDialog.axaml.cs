using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Management dialog for the static Diffusion Nexus Core instance. Two tabs: guided image
/// pipelines (e.g. Anime-To-Real) and captioning models. Mirrors the ComfyUI Workloads dialog
/// visually so the surfaces feel like one family.
/// </summary>
public partial class CoreWorkloadsDialog : Window
{
    public CoreWorkloadsDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
