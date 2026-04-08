using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Controls;

/// <summary>
/// Avalonia UserControl that wraps a LibVLCSharp VideoView with transport controls.
/// Bind its DataContext to a <see cref="ViewModels.VideoPlayerViewModel"/>.
/// </summary>
public partial class VideoPlayerControl : UserControl
{
    public VideoPlayerControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
