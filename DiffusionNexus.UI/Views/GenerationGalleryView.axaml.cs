using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Generation gallery mosaic gallery view.
/// </summary>
public partial class GenerationGalleryView : UserControl
{
    private bool _isInitialized;
    private ScrollViewer? _galleryScrollViewer;
    private Point _dragStartPoint;
    private bool _isDragPending;
    private GenerationGalleryMediaItemViewModel? _deferredSelectItem;

    /// <summary>
    /// Minimum distance in pixels the pointer must travel before initiating a drag operation.
    /// </summary>
    private const double DragThreshold = 8.0;

    public GenerationGalleryView()
    {
        InitializeComponent();
        Focusable = true;
        AttachedToVisualTree += OnAttachedToVisualTree;
        KeyDown += OnKeyDown;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_isInitialized)
        {
            _isInitialized = true;

            if (DataContext is GenerationGalleryViewModel vm)
            {
                var window = this.VisualRoot as Window ?? TopLevel.GetTopLevel(this) as Window;
                if (window is not null)
                {
                    vm.DialogService = new DialogService(window);
                }
            }

            _galleryScrollViewer = this.FindControl<ScrollViewer>("GalleryScrollViewer");
            if (_galleryScrollViewer is not null)
            {
                _galleryScrollViewer.ScrollChanged += OnGalleryScrollChanged;
            }
        }
    }

    private void OnGalleryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_galleryScrollViewer is null || DataContext is not GenerationGalleryViewModel vm)
            return;

        if (!vm.HasMoreItems)
            return;

        // Load more when within 200px of the bottom
        var distanceToBottom = _galleryScrollViewer.Extent.Height
                             - _galleryScrollViewer.Viewport.Height
                             - _galleryScrollViewer.Offset.Y;

        if (distanceToBottom < 200)
        {
            vm.LoadMoreItems();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnMediaCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not GenerationGalleryMediaItemViewModel item) return;
        if (DataContext is not GenerationGalleryViewModel vm) return;

        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        var props = e.GetCurrentPoint(border).Properties;
        if (!props.IsLeftButtonPressed) return;

        var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // When clicking an already-selected item without modifiers, defer the
        // "clear others and select only this" to pointer-release. This preserves
        // the multi-selection if the user drags instead of clicking.
        _deferredSelectItem = null;
        if (!isCtrlPressed && !isShiftPressed && item.IsSelected && vm.SelectionCount > 1)
        {
            _deferredSelectItem = item;
        }
        else
        {
            vm.SelectWithModifiers(item, isShiftPressed, isCtrlPressed);
        }

        // Track drag start for drag-out to other apps
        _dragStartPoint = e.GetPosition(this);
        _isDragPending = true;
        border.PointerMoved += OnMediaCardPointerMoved;
        border.PointerReleased += OnMediaCardPointerReleased;

        // Take focus so Ctrl+C works after clicking a tile
        Focus();
        e.Handled = true;
    }

    private async void OnMediaCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragPending) return;
        if (sender is not Border border) return;
        if (DataContext is not GenerationGalleryViewModel vm) return;

        var currentPos = e.GetPosition(this);
        var delta = currentPos - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _isDragPending = false;
        _deferredSelectItem = null;
        border.PointerMoved -= OnMediaCardPointerMoved;
        border.PointerReleased -= OnMediaCardPointerReleased;

        var filePaths = vm.GetSelectedFilePaths();
        if (filePaths.Count == 0) return;

        var dataObject = new DataObject();
        var storageItems = await ResolveStorageItemsAsync(filePaths);
        if (storageItems.Count > 0)
        {
            dataObject.Set(DataFormats.Files, storageItems);
            // TODO: Linux Implementation for drag-out file support
            await DragDrop.DoDragDrop(e, dataObject, DragDropEffects.Copy);
        }
    }

    private void OnMediaCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragPending = _isDragPending;
        _isDragPending = false;

        if (sender is Border border)
        {
            border.PointerMoved -= OnMediaCardPointerMoved;
            border.PointerReleased -= OnMediaCardPointerReleased;
        }

        // No drag happened — apply the deferred single-select now
        if (wasDragPending && _deferredSelectItem is not null
            && DataContext is GenerationGalleryViewModel vm)
        {
            vm.SelectWithModifiers(_deferredSelectItem, isShiftPressed: false, isCtrlPressed: false);
        }

        _deferredSelectItem = null;
    }

    private void OnMediaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;

        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        var parent = border.Parent;
        while (parent is not null)
        {
            if (parent.DataContext is GenerationGalleryMediaItemViewModel item)
            {
                if (DataContext is GenerationGalleryViewModel vm)
                {
                    vm.OpenImageViewerCommand.Execute(item);
                }
                e.Handled = true;
                return;
            }
            parent = parent.Parent as Control;
        }
    }

    /// <summary>
    /// Handles keyboard shortcuts: Ctrl+C (copy files), Ctrl+A (select all), Escape (clear selection).
    /// </summary>
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not GenerationGalleryViewModel vm) return;

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await CopySelectedFilesToClipboardAsync(vm);
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

    /// <summary>
    /// Copies the selected gallery files to the system clipboard so they can be pasted in Explorer or other apps.
    /// </summary>
    private async Task CopySelectedFilesToClipboardAsync(GenerationGalleryViewModel vm)
    {
        var filePaths = vm.GetSelectedFilePaths();
        if (filePaths.Count == 0) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var dataObject = new DataObject();
        var storageItems = await ResolveStorageItemsAsync(filePaths);
        if (storageItems.Count > 0)
        {
            dataObject.Set(DataFormats.Files, storageItems);
            // TODO: Linux Implementation for clipboard file copy
            await clipboard.SetDataObjectAsync(dataObject);
        }
    }

    /// <summary>
    /// Resolves file paths to <see cref="IStorageItem"/> instances for clipboard and drag-and-drop operations.
    /// </summary>
    private async Task<List<IStorageItem>> ResolveStorageItemsAsync(IReadOnlyList<string> filePaths)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null) return [];

        var items = new List<IStorageItem>(filePaths.Count);
        foreach (var path in filePaths)
        {
            try
            {
                var file = await storageProvider.TryGetFileFromPathAsync(new Uri($"file:///{path.Replace('\\', '/').TrimStart('/')}"));
                if (file is not null)
                    items.Add(file);
            }
            catch
            {
                // Skip files that can't be resolved (deleted, inaccessible)
            }
        }

        return items;
    }
}
