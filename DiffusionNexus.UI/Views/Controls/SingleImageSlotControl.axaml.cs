using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    /// <summary>
    /// Pastes an image from the clipboard (e.g. a screenshot) into the slot, so the user doesn't have to
    /// save it to disk first. Prefers the clipboard's PNG payload (what modern snippers provide); falls
    /// back to a device-independent bitmap (plain PrintScreen). The bytes are written to a temp PNG that
    /// the generation reads like any other reference path.
    /// </summary>
    // Avalonia 12 deprecated the string-format clipboard API in favour of typed DataFormat /
    // TryGetDataAsync. The string-format reads below still work and are the simplest way to pull a
    // clipboard PNG/DIB by its platform format name; migrate to DataFormat if the old API is removed.
#pragma warning disable CS0618
    private async void OnPasteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            var formats = await clipboard.GetFormatsAsync();

            // 0) A copied image FILE (e.g. Ctrl+C in Explorer) — the clipboard holds a file reference,
            // not pixels. Use the first image file's path directly (original quality, no temp copy).
            if (formats.Contains(DataFormats.Files)
                && await clipboard.GetDataAsync(DataFormats.Files) is IEnumerable<Avalonia.Platform.Storage.IStorageItem> items)
            {
                var file = items.OfType<IStorageFile>().Select(f => f.Path.LocalPath).FirstOrDefault(IsImageFile);
                if (file is not null)
                {
                    ImagePath = file;
                    return;
                }
            }

            // 1) PNG (Snipping Tool, ShareX, browsers, …) — write the bytes straight to a temp .png.
            var png = await TryGetBytes(clipboard, formats, "PNG")
                   ?? await TryGetBytes(clipboard, formats, "image/png");
            if (png is { Length: > 0 })
            {
                ImagePath = WriteTempPng(png);
                return;
            }

            // 2) Device-independent bitmap (plain PrintScreen) — wrap as a BMP and re-encode to PNG.
            var dib = await TryGetBytes(clipboard, formats, "DeviceIndependentBitmap")
                   ?? await TryGetBytes(clipboard, formats, "DeviceIndependentBitmapV5");
            if (dib is { Length: > 0 } && DibToBmp(dib) is { } bmp)
            {
                using var ms = new MemoryStream(bmp);
                using var bitmap = new Bitmap(ms);
                var path = TempPath();
                bitmap.Save(path); // Avalonia saves as PNG
                ImagePath = path;
            }
        }
        catch
        {
            // Clipboard read / decode failures are non-fatal — the user can still use Add or drag-drop.
        }
    }

    private static async System.Threading.Tasks.Task<byte[]?> TryGetBytes(IClipboard cb, string[] formats, string format)
    {
        if (!formats.Contains(format)) return null;
        return await cb.GetDataAsync(format) as byte[];
    }
#pragma warning restore CS0618

    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiffusionNexus", "clipboard-refs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"clip_{Guid.NewGuid():N}.png");
    }

    private static string WriteTempPng(byte[] png)
    {
        var path = TempPath();
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>
    /// Prepends a 14-byte BITMAPFILEHEADER to a clipboard CF_DIB so it can be decoded as a BMP. Handles
    /// the common screenshot cases (BITMAPINFOHEADER 24/32-bit, BI_BITFIELDS masks, BITMAPV5HEADER, and
    /// a low-bit-depth palette). Returns null if the buffer is too small to be a DIB.
    /// </summary>
    private static byte[]? DibToBmp(byte[] dib)
    {
        if (dib.Length < 40) return null;

        int dibHeaderSize = BitConverter.ToInt32(dib, 0);
        short bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);

        // BI_BITFIELDS adds three DWORD colour masks only after a plain BITMAPINFOHEADER (40);
        // BITMAPV5HEADER (124) already includes the masks.
        int maskBytes = (dibHeaderSize == 40 && compression == 3) ? 12 : 0;
        int paletteEntries = bitCount <= 8 ? (clrUsed != 0 ? clrUsed : 1 << bitCount) : 0;
        int pixelOffset = 14 + dibHeaderSize + maskBytes + paletteEntries * 4;

        using var ms = new MemoryStream(14 + dib.Length);
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(14 + dib.Length); // total file size
        bw.Write(0);               // reserved
        bw.Write(pixelOffset);     // offset to pixel data
        bw.Write(dib);
        bw.Flush();
        return ms.ToArray();
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
