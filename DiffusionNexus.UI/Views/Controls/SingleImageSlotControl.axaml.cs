using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// A single fixed image "slot": holds exactly one image (with a clear button) or shows a placeholder
/// prompting the user to add one. Distinct from the multi-image <see cref="ImageListInputControl"/> — used
/// for the Image-to-Image reference slots, where each slot is one reference applied to every input image.
/// Accepts an image via a file picker or drag-and-drop.
/// </summary>
public partial class SingleImageSlotControl : UserControl
{
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

    /// <summary>The selected image path, or null when the slot is empty. Two-way by default.</summary>
    public static readonly StyledProperty<string?> ImagePathProperty =
        AvaloniaProperty.Register<SingleImageSlotControl, string?>(
            nameof(ImagePath), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Placeholder text shown when the slot is empty (e.g. "Reference 1").</summary>
    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<SingleImageSlotControl, string>(nameof(Placeholder), "Add image");

    public string? ImagePath
    {
        get => GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Clears the slot (bound by the remove button).</summary>
    public ICommand ClearCommand { get; }

    public SingleImageSlotControl()
    {
        ClearCommand = new RelayCommand(() => ImagePath = null);
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a reference image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image Files")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp", "*.tiff", "*.tif"]
                }
            ]
        });

        var path = files.Count > 0 ? files[0].Path.LocalPath : null;
        if (path is not null && IsImageFile(path))
            ImagePath = path;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        SetHighlight(e.DragEffects == DragDropEffects.Copy);
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => SetHighlight(false);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetHighlight(false);
        var path = e.DataTransfer?.TryGetFiles()?
            .OfType<IStorageFile>()
            .Select(f => f.Path.LocalPath)
            .FirstOrDefault(IsImageFile);
        if (path is not null)
            ImagePath = path;
    }

    private void SetHighlight(bool active)
    {
        var brush = active ? new SolidColorBrush(Color.Parse("#4CAF50")) : new SolidColorBrush(Color.Parse("#444"));
        if (this.FindControl<Border>("SlotBorder") is { } b) b.BorderBrush = brush;
    }

    private static bool HasImageFile(DragEventArgs e) =>
        e.DataTransfer?.TryGetFiles()?.OfType<IStorageFile>().Any(f => IsImageFile(f.Path.LocalPath)) ?? false;

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
