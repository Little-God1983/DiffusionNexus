using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Control for displaying a single model as a tile in the grid.
/// </summary>
public partial class ModelTileControl : UserControl
{
    /// <summary>
    /// The VM the control most recently activated. Tracked so we can deactivate
    /// it when DataContext changes (container recycle by ItemsRepeater) or when
    /// the container is detached.
    /// </summary>
    private ModelTileViewModel? _activeVm;

    public ModelTileControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// When the container enters the visual tree (e.g. scrolls into view inside an
    /// ItemsRepeater), activate the bound tile so its thumbnail loads. When it
    /// leaves, deactivate so the decoded <see cref="Avalonia.Media.Imaging.Bitmap"/>
    /// and the encoded byte cache are released. Caps total thumbnail memory at
    /// ~visible-tile-count regardless of how many LoRAs are installed.
    /// </summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindAndActivate();
        DataContextChanged += OnDataContextChangedWhileAttached;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        DataContextChanged -= OnDataContextChangedWhileAttached;
        _activeVm?.Deactivate();
        _activeVm = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChangedWhileAttached(object? sender, System.EventArgs e)
    {
        BindAndActivate();
    }

    /// <summary>
    /// Swaps the currently-active VM. Deactivates the old VM (if any) and activates
    /// the new one. Idempotent when the DataContext hasn't actually changed.
    /// </summary>
    private void BindAndActivate()
    {
        var newVm = DataContext as ModelTileViewModel;
        if (ReferenceEquals(newVm, _activeVm)) return;
        _activeVm?.Deactivate();
        _activeVm = newVm;
        _activeVm?.Activate();
    }
}
