using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// One file decoded for <see cref="ImageEditorCore.AddDecodedLayers"/>. <see cref="Bitmap"/> is
/// null when the file could not be read or decoded. Produced off the UI thread by
/// <see cref="ImageEditorCore.DecodeLayerFiles"/>; the consumer owns and disposes it.
/// </summary>
public sealed class LayerImportItem(string path, SKBitmap? bitmap) : IDisposable
{
    public string Path { get; } = path;

    public SKBitmap? Bitmap { get; } = bitmap;

    public void Dispose() => Bitmap?.Dispose();
}
