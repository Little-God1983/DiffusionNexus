using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Shown by the Installed tab's "Download LoRA" button since downloading from a link moved to
/// Browse Civitai (issue #630). The button stays so users who look for it are told where it went.
/// </summary>
public partial class DownloadLoraMovedDialog : Window
{
    public DownloadLoraMovedDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>True when the user chose "Go to Browse Civitai".</summary>
    public bool GoToBrowse { get; private set; }

    private void OnGoToBrowseClick(object? sender, RoutedEventArgs e)
    {
        GoToBrowse = true;
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
