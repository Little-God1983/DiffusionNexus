using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog window displaying the captioning models available to the Diffusion
/// Nexus Core capability, plus every directory currently being scanned for
/// GGUF/mmproj files. Mirrors <see cref="WorkloadsDialog"/> visually so the
/// two surfaces feel like part of the same family.
/// </summary>
public partial class CaptioningModelsDialog : Window
{
    public CaptioningModelsDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
