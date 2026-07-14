using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable input control for picking one or more loose images (not from a dataset).
/// <para>
/// Accepts multiple images via drag-and-drop or a file picker, shows them as a
/// selectable thumbnail list with a large preview of the selected image, and lets
/// the user remove individual images or clear the whole list.
/// </para>
/// <para>
/// The control mutates the bound <see cref="ImagePaths"/> collection in place, so the
/// owning ViewModel observes changes through that same <see cref="ObservableCollection{T}"/>.
/// Used by the Captioning, Batch Upscale, Batch Crop/Scale and Workflows (pipeline) screens.
/// </para>
/// <para>
/// <b>Threading:</b> thumbnails and the large preview are decoded on background threads and
/// assigned back on the UI thread, so adding a large batch of images never blocks the UI. The
/// control mirrors <see cref="ImagePaths"/> (a collection of <see cref="string"/> paths) into
/// <see cref="Thumbnails"/> (items carrying an async-loaded <see cref="Bitmap"/>); never bind a
/// bare path through a synchronous converter here — that decodes on the UI thread and freezes it.
/// </para>
/// </summary>
public partial class ImageListInputControl : UserControl
{
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

    /// <summary>Pixel width thumbnails are decoded to (matches the 68px slot at ~2x for crispness).</summary>
    private const int ThumbnailPixelWidth = 160;

    /// <summary>
    /// Bounds how many images decode at once so a large drop doesn't saturate the disk/CPU.
    /// Decoding is off the UI thread regardless; this only paces how fast thumbnails fill in.
    /// </summary>
    private readonly SemaphoreSlim _decodeGate = new(4);

    private CancellationTokenSource? _previewCts;

    /// <summary>
    /// Defines the <see cref="ImagePaths"/> property — the collection of selected image
    /// file paths. Mutated in place by the control.
    /// </summary>
    public static readonly StyledProperty<ObservableCollection<string>?> ImagePathsProperty =
        AvaloniaProperty.Register<ImageListInputControl, ObservableCollection<string>?>(nameof(ImagePaths));

    /// <summary>
    /// Defines the <see cref="SelectedImagePath"/> property — the image shown in the preview.
    /// </summary>
    public static readonly StyledProperty<string?> SelectedImagePathProperty =
        AvaloniaProperty.Register<ImageListInputControl, string?>(
            nameof(SelectedImagePath), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Defines the <see cref="HasImages"/> property — true when at least one image is loaded.
    /// Driven by the control; bind one-way for visibility.
    /// </summary>
    public static readonly StyledProperty<bool> HasImagesProperty =
        AvaloniaProperty.Register<ImageListInputControl, bool>(nameof(HasImages));

    /// <summary>
    /// Defines the <see cref="CountText"/> property — a short "N image(s)" summary.
    /// </summary>
    public static readonly StyledProperty<string> CountTextProperty =
        AvaloniaProperty.Register<ImageListInputControl, string>(nameof(CountText), string.Empty);

    /// <summary>
    /// Defines the <see cref="SelectedPreview"/> property — the decoded bitmap for the large
    /// preview of <see cref="SelectedImagePath"/>. Loaded off the UI thread by the control.
    /// </summary>
    public static readonly StyledProperty<Bitmap?> SelectedPreviewProperty =
        AvaloniaProperty.Register<ImageListInputControl, Bitmap?>(nameof(SelectedPreview));

    /// <summary>
    /// Gets or sets the collection of selected image paths. The control adds, removes and
    /// clears items on this collection directly.
    /// </summary>
    public ObservableCollection<string>? ImagePaths
    {
        get => GetValue(ImagePathsProperty);
        set => SetValue(ImagePathsProperty, value);
    }

    /// <summary>
    /// Gets or sets the path of the image currently shown in the preview.
    /// </summary>
    public string? SelectedImagePath
    {
        get => GetValue(SelectedImagePathProperty);
        set => SetValue(SelectedImagePathProperty, value);
    }

    /// <summary>
    /// Gets whether at least one image is loaded.
    /// </summary>
    public bool HasImages
    {
        get => GetValue(HasImagesProperty);
        private set => SetValue(HasImagesProperty, value);
    }

    /// <summary>
    /// Gets a short "N image(s)" summary of the loaded images.
    /// </summary>
    public string CountText
    {
        get => GetValue(CountTextProperty);
        private set => SetValue(CountTextProperty, value);
    }

    /// <summary>
    /// Gets the decoded bitmap for the large preview. Set by the control after an off-thread decode.
    /// </summary>
    public Bitmap? SelectedPreview
    {
        get => GetValue(SelectedPreviewProperty);
        private set => SetValue(SelectedPreviewProperty, value);
    }

    /// <summary>
    /// The thumbnail items shown in the list — one per entry in <see cref="ImagePaths"/>, each
    /// carrying its own asynchronously decoded <see cref="Bitmap"/>. The XAML binds this instead of
    /// binding paths through a converter, so decoding never happens on the UI thread.
    /// </summary>
    public ObservableCollection<ImageThumbnailItem> Thumbnails { get; } = [];

    /// <summary>
    /// Command bound by the per-thumbnail remove button.
    /// </summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Command bound by the "Clear all" button.
    /// </summary>
    public ICommand ClearCommand { get; }

    /// <summary>
    /// Command bound by a thumbnail to select it for the large preview.
    /// </summary>
    public ICommand SelectCommand { get; }

    public ImageListInputControl()
    {
        RemoveCommand = new RelayCommand<string>(RemoveImage);
        ClearCommand = new RelayCommand(ClearImages);
        SelectCommand = new RelayCommand<string>(SelectImage);

        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ImagePathsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCollection)
                oldCollection.CollectionChanged -= OnImagePathsChanged;

            if (change.NewValue is INotifyCollectionChanged newCollection)
                newCollection.CollectionChanged += OnImagePathsChanged;

            RebuildThumbnails();
            UpdateState();
        }
        else if (change.Property == SelectedImagePathProperty)
        {
            LoadPreview(change.GetNewValue<string?>());
        }
    }

    private void OnImagePathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (string path in e.NewItems.OfType<string>())
                    AddThumbnail(path);
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (string path in e.OldItems.OfType<string>())
                    RemoveThumbnail(path);
                break;

            // Replace / Move / Reset (Clear) and anything unexpected: rebuild from scratch. These are
            // rare for this control (paths are appended or removed one at a time), so the cost is fine.
            default:
                RebuildThumbnails();
                break;
        }

        UpdateState();
    }

    /// <summary>
    /// Recomputes <see cref="HasImages"/>, <see cref="CountText"/> and keeps
    /// <see cref="SelectedImagePath"/> pointing at a valid entry.
    /// </summary>
    private void UpdateState()
    {
        var paths = ImagePaths;
        var count = paths?.Count ?? 0;

        HasImages = count > 0;
        CountText = count == 1 ? "1 image" : $"{count} images";

        // Keep the preview selection valid: clear it when empty, default to the
        // first entry when nothing valid is selected.
        if (count == 0)
        {
            if (SelectedImagePath is not null) SelectedImagePath = null;
        }
        else if (SelectedImagePath is null || paths!.IndexOf(SelectedImagePath) < 0)
        {
            SelectedImagePath = paths![0];
        }
    }

    #region Thumbnail mirroring (off-thread decode)

    /// <summary>Clears and rebuilds <see cref="Thumbnails"/> from the current <see cref="ImagePaths"/>.</summary>
    private void RebuildThumbnails()
    {
        foreach (var item in Thumbnails)
        {
            item.Cancel();
            DeferDispose(item.Image);
        }
        Thumbnails.Clear();

        var paths = ImagePaths;
        if (paths is null) return;

        foreach (var path in paths)
            AddThumbnail(path);
    }

    private void AddThumbnail(string path)
    {
        var item = new ImageThumbnailItem(path);
        Thumbnails.Add(item);
        _ = LoadThumbnailAsync(item);
    }

    private void RemoveThumbnail(string path)
    {
        var item = Thumbnails.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        item.Cancel();
        Thumbnails.Remove(item);
        DeferDispose(item.Image);
    }

    /// <summary>Decodes a single thumbnail off the UI thread and assigns it back on the UI thread.</summary>
    private async Task LoadThumbnailAsync(ImageThumbnailItem item)
    {
        var ct = item.Token;
        Bitmap? bmp = null;
        try
        {
            await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bmp = await Task.Run(() => EfficientImageDecoder.DecodeThumbnail(item.Path, ThumbnailPixelWidth), ct)
                                .ConfigureAwait(false);
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (OperationCanceledException) { return; }
        catch { return; } // undecodable image — leave the placeholder slot

        if (ct.IsCancellationRequested)
        {
            DeferDispose(bmp);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested)
                DeferDispose(bmp);
            else
                item.Image = bmp;
        });
    }

    #endregion

    #region Preview (off-thread decode)

    /// <summary>Starts an off-thread decode of the large preview for <paramref name="path"/>.</summary>
    private void LoadPreview(string? path)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SetPreview(null);
            return;
        }

        _ = LoadPreviewAsync(path, _previewCts.Token);
    }

    private async Task LoadPreviewAsync(string path, CancellationToken ct)
    {
        Bitmap? bmp;
        try
        {
            bmp = await Task.Run(() => EfficientImageDecoder.DecodeForDisplay(path), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch { return; }

        if (ct.IsCancellationRequested)
        {
            DeferDispose(bmp);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested)
                DeferDispose(bmp);
            else
                SetPreview(bmp);
        });
    }

    /// <summary>Swaps in a new preview bitmap on the UI thread, disposing the one it replaces.</summary>
    private void SetPreview(Bitmap? bmp)
    {
        var previous = SelectedPreview;
        SelectedPreview = bmp;
        if (!ReferenceEquals(previous, bmp))
            DeferDispose(previous);
    }

    #endregion

    /// <summary>
    /// Disposes a bitmap after the current render pass so a control still displaying it isn't torn
    /// out from under the renderer. No-op for null.
    /// </summary>
    private static void DeferDispose(Bitmap? bmp)
    {
        if (bmp is null) return;
        Dispatcher.UIThread.Post(bmp.Dispose, DispatcherPriority.Background);
    }

    private void RemoveImage(string? path)
    {
        if (path is null) return;
        ImagePaths?.Remove(path);
    }

    private void SelectImage(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            SelectedImagePath = path;
    }

    private void ClearImages() => ImagePaths?.Clear();

    /// <summary>
    /// Adds the given paths to <see cref="ImagePaths"/>, skipping non-images and duplicates.
    /// Creates the backing collection if the consumer has not supplied one.
    /// </summary>
    private void AddPaths(IEnumerable<string> paths)
    {
        var target = ImagePaths;
        if (target is null)
        {
            target = [];
            ImagePaths = target;
        }

        foreach (var path in paths)
        {
            if (!IsImageFile(path) || target.Contains(path)) continue;
            target.Add(path);
        }
    }

    private async void OnAddImagesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Images",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Image Files")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp", "*.tiff", "*.tif"]
                }
            ]
        });

        if (files.Count > 0)
            AddPaths(files.Select(f => f.Path.LocalPath));
    }

    #region Drag & drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasImageFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        SetZoneHighlight(e.DragEffects == DragDropEffects.Copy);
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => SetZoneHighlight(false);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetZoneHighlight(false);

        var files = e.DataTransfer?.TryGetFiles();
        if (files is null) return;

        var paths = files
            .OfType<IStorageFile>()
            .Select(f => f.Path.LocalPath)
            .Where(IsImageFile)
            .ToList();

        if (paths.Count > 0)
            AddPaths(paths);
    }

    /// <summary>Highlights whichever drop zone is currently visible.</summary>
    private void SetZoneHighlight(bool active)
    {
        var brush = active ? new SolidColorBrush(Color.Parse("#4CAF50")) : new SolidColorBrush(Color.Parse("#444"));
        if (this.FindControl<Border>("EmptyDropZone") is { } empty) empty.BorderBrush = brush;
        if (this.FindControl<Border>("LoadedZone") is { } loaded) loaded.BorderBrush = brush;
    }

    private static bool HasImageFiles(DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files is null) return false;

        return files.OfType<IStorageFile>().Any(f => IsImageFile(f.Path.LocalPath));
    }

    #endregion

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Multi-value converter for the per-thumbnail selected highlight: returns an accent brush
    /// when the item path (values[0]) equals the control's <see cref="SelectedImagePath"/>
    /// (values[1]), otherwise transparent.
    /// </summary>
    public static readonly IMultiValueConverter SelectedHighlightConverter = new SelectedHighlightConverterImpl();

    private sealed class SelectedHighlightConverterImpl : IMultiValueConverter
    {
        private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4CAF50"));

        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 2 && values[0] is string item && values[1] is string selected &&
                string.Equals(item, selected, StringComparison.OrdinalIgnoreCase))
            {
                return Accent;
            }

            return Brushes.Transparent;
        }
    }
}

/// <summary>
/// A single thumbnail row for <see cref="ImageListInputControl"/>: the source path plus its
/// asynchronously decoded <see cref="Image"/> (null until the background decode completes, which
/// shows the placeholder slot). Each item owns a cancellation token so a removed/replaced image
/// stops its in-flight decode instead of assigning a bitmap nobody is showing.
/// </summary>
public sealed partial class ImageThumbnailItem : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    private Bitmap? _image;

    public string Path { get; }

    public ImageThumbnailItem(string path) => Path = path;

    /// <summary>Token that is cancelled when this item is removed; observed by the decode task.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Cancels this item's pending decode. Called when the item leaves the list.</summary>
    public void Cancel()
    {
        // Not disposed: the decode task may still read Token briefly; a token-only CTS holds no
        // unmanaged resources, so letting it be collected with the item is safe.
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
    }
}
