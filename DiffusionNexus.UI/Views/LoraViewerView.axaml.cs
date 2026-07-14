using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// View for browsing and managing LoRA models.
/// </summary>
public partial class LoraViewerView : UserControl
{
    private ScrollViewer? _tileScrollViewer;

    /// <summary>
    /// Approximate height of one row of tiles in DIPs. Used to estimate which item
    /// index is at the top of the viewport. The tile control's design height is
    /// 340 px + a 12 px outer margin from the tile-template Border.
    /// </summary>
    private const double EstimatedRowHeight = 360;

    /// <summary>Approximate width of one tile slot (250 + 12 margin).</summary>
    private const double EstimatedTileWidth = 262;

    /// <summary>
    /// How close to the bottom of the loaded window (in tile indices) the user has
    /// to be before we slide the window forward. Set wider than the slide step so
    /// the trigger has room to fire repeatedly as the user scrolls — at 75 with
    /// step=50, the trigger zone always covers the "freshly loaded" buffer.
    /// </summary>
    private const int SlideTriggerDistance = 75;

    /// <summary>
    /// Guards against re-entrant slides — sliding sets the new scroll offset,
    /// which can fire a ScrollChanged event that would otherwise trigger another
    /// slide immediately.
    /// </summary>
    private bool _slideInFlight;

    public LoraViewerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tileScrollViewer = this.FindControl<ScrollViewer>("TileScrollViewer");
        if (_tileScrollViewer is not null)
        {
            _tileScrollViewer.ScrollChanged += OnTileScrollChanged;
        }

        if (DataContext is LoraViewerViewModel vm)
        {
            vm.OnViewAttached();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_tileScrollViewer is not null)
        {
            _tileScrollViewer.ScrollChanged -= OnTileScrollChanged;
        }
        _tileScrollViewer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTileScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_slideInFlight) return;
        if (DataContext is not LoraViewerViewModel vm) return;
        if (_tileScrollViewer is null) return;

        var sv = _tileScrollViewer;
        var viewportWidth = sv.Viewport.Width;
        var tilesPerRow = Math.Max(1, (int)(viewportWidth / EstimatedTileWidth));

        // Index (within FilteredTiles) of the first tile visible at the top of the
        // viewport, given the current scroll offset.
        var firstVisibleIdx = (int)(sv.Offset.Y / EstimatedRowHeight) * tilesPerRow;
        var windowedCount = vm.FilteredTiles.Count;

        // Forward: user has scrolled into the trailing edge of the window.
        if (windowedCount - firstVisibleIdx <= SlideTriggerDistance && vm.HasMoreForward)
        {
            SlideForward(vm, tilesPerRow);
        }
        // Backward: user has scrolled to (or near) the top, and earlier items exist.
        else if (firstVisibleIdx < SlideTriggerDistance && vm.HasMoreBackward)
        {
            SlideBackward(vm, tilesPerRow);
        }
    }

    /// <summary>
    /// Triggers a forward slide on the VM and compensates the scroll offset so the
    /// tiles currently under the user's eye stay visually anchored. Without this,
    /// removing N items from the start of the list pulls the rest up by N rows /
    /// tilesPerRow, jolting the viewport up.
    /// </summary>
    private void SlideForward(LoraViewerViewModel vm, int tilesPerRow)
    {
        _slideInFlight = true;
        try
        {
            vm.SlideForward();
            var removed = vm.LastSlideForwardCount;
            if (removed > 0 && _tileScrollViewer is not null)
            {
                var rowsRemoved = (double)removed / tilesPerRow;
                var newY = Math.Max(0, _tileScrollViewer.Offset.Y - rowsRemoved * EstimatedRowHeight);
                _tileScrollViewer.Offset = _tileScrollViewer.Offset.WithY(newY);
            }
        }
        finally
        {
            _slideInFlight = false;
        }
    }

    private void SlideBackward(LoraViewerViewModel vm, int tilesPerRow)
    {
        _slideInFlight = true;
        try
        {
            vm.SlideBackward();
            var added = vm.LastSlideBackwardCount;
            if (added > 0 && _tileScrollViewer is not null)
            {
                var rowsAdded = (double)added / tilesPerRow;
                var newY = _tileScrollViewer.Offset.Y + rowsAdded * EstimatedRowHeight;
                _tileScrollViewer.Offset = _tileScrollViewer.Offset.WithY(newY);
            }
        }
        finally
        {
            _slideInFlight = false;
        }
    }
}
