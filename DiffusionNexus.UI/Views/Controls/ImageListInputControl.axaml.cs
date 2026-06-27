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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

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
/// Used by the Captioning, Batch Upscale and Batch Crop/Scale tabs.
/// </para>
/// </summary>
public partial class ImageListInputControl : UserControl
{
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

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

            UpdateState();
        }
    }

    private void OnImagePathsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

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
