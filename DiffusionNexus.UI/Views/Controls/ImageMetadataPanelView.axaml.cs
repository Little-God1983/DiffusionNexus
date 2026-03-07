using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Collapsible side panel that displays ComfyUI generation metadata for the current image.
/// </summary>
public partial class ImageMetadataPanelView : UserControl
{
    public ImageMetadataPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ImageMetadataPanelViewModel vm)
        {
            vm.SetClipboardAction(CopyToClipboardAsync);
        }
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
