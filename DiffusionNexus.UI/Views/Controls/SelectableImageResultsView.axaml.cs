using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels.Controls;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// A multi-selectable strip of result tiles. Ctrl-click toggles a tile, Shift-click selects a range,
/// a plain click selects one (and makes it the comparison's primary). Ctrl+C copies the selected
/// images to the clipboard (paste them in Explorer); Ctrl+A selects all; Esc clears. Tiles can also be
/// dragged out to other apps. Reuses <see cref="ImageFileTransfer"/> / <see cref="ImageDragPreview"/>
/// and embeds the reusable <see cref="ImageActionsBar"/>. Bind <c>DataContext</c> to a
/// <see cref="SelectableImageResultsViewModel"/>.
/// </summary>
public partial class SelectableImageResultsView : UserControl
{
    /// <summary>Minimum pointer travel (px) before a click turns into a drag-out.</summary>
    private const double DragThreshold = 8.0;

    private Point _dragStartPoint;
    private bool _isDragPending;
    private ImageStatusItemViewModel? _deferredSelectItem;
    private readonly ImageDragPreview _dragPreview = new();

    public SelectableImageResultsView()
    {
        InitializeComponent();
        Focusable = true;
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SelectableImageResultsViewModel? ViewModel => DataContext as SelectableImageResultsViewModel;

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not ImageStatusItemViewModel item) return;
        if (ViewModel is not { } vm) return;

        // Let buttons inside a tile (if any) handle their own clicks.
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
            return;

        var props = e.GetCurrentPoint(border).Properties;
        if (!props.IsLeftButtonPressed) return;

        var isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Clicking an already-selected tile without modifiers while several are selected defers the
        // "select only this" until release, so dragging the group out preserves the multi-selection.
        _deferredSelectItem = null;
        if (!isCtrl && !isShift && item.IsSelected && vm.SelectionCount > 1)
            _deferredSelectItem = item;
        else
            vm.SelectWithModifiers(item, isShift, isCtrl);

        _dragStartPoint = e.GetPosition(this);
        _isDragPending = true;
        border.PointerMoved += OnTilePointerMoved;
        border.PointerReleased += OnTilePointerReleased;

        // Take focus so Ctrl+C works after clicking a tile.
        Focus();
        e.Handled = true;
    }

    private async void OnTilePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragPending) return;
        if (sender is not Border border) return;
        if (ViewModel is not { } vm) return;

        var delta = e.GetPosition(this) - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _isDragPending = false;
        _deferredSelectItem = null;
        border.PointerMoved -= OnTilePointerMoved;
        border.PointerReleased -= OnTilePointerReleased;

        var filePaths = vm.GetSelectedFilePaths();
        if (filePaths.Count == 0) return;

        var dragData = await ImageFileTransfer.BuildFileDragDataAsync(TopLevel.GetTopLevel(this), filePaths);
        if (dragData is null) return;

        var thumbnail = (border.DataContext as ImageStatusItemViewModel)?.Thumbnail;
        _dragPreview.Show(thumbnail, filePaths.Count);
        try
        {
            // TODO: Linux implementation for drag-out file support.
            await DragDrop.DoDragDropAsync(e, dragData, DragDropEffects.Copy);
        }
        finally
        {
            _dragPreview.Hide();
        }
    }

    private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragPending = _isDragPending;
        _isDragPending = false;

        if (sender is Border border)
        {
            border.PointerMoved -= OnTilePointerMoved;
            border.PointerReleased -= OnTilePointerReleased;
        }

        // No drag happened — apply the deferred "select only this" now.
        if (wasDragPending && _deferredSelectItem is not null && ViewModel is { } vm)
            vm.SelectWithModifiers(_deferredSelectItem, isShiftPressed: false, isCtrlPressed: false);

        _deferredSelectItem = null;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await ImageFileTransfer.CopyFilesToClipboardAsync(TopLevel.GetTopLevel(this), vm.GetSelectedFilePaths());
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.HasSelection)
        {
            vm.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
