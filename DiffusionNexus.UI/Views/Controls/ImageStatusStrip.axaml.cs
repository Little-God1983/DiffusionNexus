using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable horizontal strip of image tiles whose 2px outline colour reflects each item's
/// <see cref="ViewModels.Controls.ImageProcessingStatus"/> (pending / processing / done / failed).
/// Clicking a tile sets <see cref="SelectedItem"/> (two-way), so a host can drive a before/after
/// comparison from the selection. Extracted from the inline Batch Upscale gallery so any batch
/// feature can reuse it. Bind <see cref="ItemsSource"/> to a collection of
/// <see cref="ViewModels.Controls.ImageStatusItemViewModel"/>.
/// </summary>
public partial class ImageStatusStrip : UserControl
{
    /// <summary>The items to display (typically <c>ObservableCollection&lt;ImageStatusItemViewModel&gt;</c>).</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ImageStatusStrip, IEnumerable?>(nameof(ItemsSource));

    /// <summary>The selected tile (two-way) — drive a before/after comparison from this.</summary>
    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<ImageStatusStrip, object?>(
            nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ImageStatusStrip() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
