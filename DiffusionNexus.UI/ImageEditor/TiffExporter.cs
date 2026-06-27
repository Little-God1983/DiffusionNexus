using BitMiracle.LibTiff.Classic;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Provides TIFF file export/import functionality with multi-layer support.
/// Uses LibTiff.NET for proper multi-page/layered TIFF creation, and the robust
/// <c>ReadRGBAImageOriented</c> reader so arbitrary third-party TIFFs (tiled, planar,
/// compressed, palette, CMYK, etc.) load correctly — not just app-written ones.
/// All operations report progress/errors through the optional <see cref="IUnifiedLogger"/>.
/// </summary>
public static class TiffExporter
{
    private const string LogSource = "TiffExporter";

    /// <summary>
    /// Saves layers to a multi-page TIFF file.
    /// Each layer is saved as a separate page with metadata.
    /// </summary>
    /// <param name="layers">The layer stack to export.</param>
    /// <param name="filePath">The output file path.</param>
    /// <param name="logger">Optional unified logger for diagnostics.</param>
    /// <returns>True if saved successfully.</returns>
    public static bool SaveLayeredTiff(LayerStack layers, string filePath, IUnifiedLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(filePath);

        if (layers.Count == 0)
        {
            logger?.Warn(LogCategory.General, LogSource, "SaveLayeredTiff: no layers to write.", filePath);
            return false;
        }

        try
        {
            using var tiff = Tiff.Open(filePath, "w");
            if (tiff == null)
            {
                logger?.Error(LogCategory.General, LogSource, $"SaveLayeredTiff: Tiff.Open returned null for {filePath}");
                return false;
            }

            var pagesWritten = 0;
            for (var pageIndex = 0; pageIndex < layers.Count; pageIndex++)
            {
                var layer = layers[pageIndex];
                if (layer.Bitmap == null)
                    continue;

                WritePage(tiff, layer.Bitmap, CreateLayerMetadata(layer, pageIndex), pageIndex, layers.Count);
                pagesWritten++;
            }

            logger?.Info(LogCategory.General, LogSource,
                $"Saved {pagesWritten}-page TIFF.", filePath);
            return pagesWritten > 0;
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.General, LogSource, $"SaveLayeredTiff failed for {filePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// Saves a single flattened image to TIFF format.
    /// </summary>
    /// <param name="bitmap">The bitmap to save.</param>
    /// <param name="filePath">The output file path.</param>
    /// <param name="logger">Optional unified logger for diagnostics.</param>
    /// <returns>True if saved successfully.</returns>
    public static bool SaveFlattenedTiff(SKBitmap bitmap, string filePath, IUnifiedLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            using var tiff = Tiff.Open(filePath, "w");
            if (tiff == null)
            {
                logger?.Error(LogCategory.General, LogSource, $"SaveFlattenedTiff: Tiff.Open returned null for {filePath}");
                return false;
            }

            WritePage(tiff, bitmap, metadata: null, pageIndex: 0, pageCount: 1);
            logger?.Info(LogCategory.General, LogSource, "Saved flattened TIFF.", filePath);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.General, LogSource, $"SaveFlattenedTiff failed for {filePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes a single RGBA page (8-bit, LZW, straight/unassociated alpha) to an open TIFF.
    /// </summary>
    private static void WritePage(Tiff tiff, SKBitmap bitmap, string? metadata, int pageIndex, int pageCount)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;

        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 4); // RGBA
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
        // Straight (non-premultiplied) alpha: SKBitmap.Pixels returns unassociated SKColor
        // channels, so the file must be tagged UNASSALPHA (tagging ASSOCALPHA made viewers
        // such as GIMP mis-composite semi-transparent pixels).
        tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
        // Let LibTiff pick a standard strip size (~8 KB) instead of one giant strip, which is
        // friendlier to third-party readers.
        tiff.SetField(TiffTag.ROWSPERSTRIP, tiff.DefaultStripSize(0));

        if (metadata is not null)
            tiff.SetField(TiffTag.IMAGEDESCRIPTION, metadata);

        if (pageCount > 1)
            tiff.SetField(TiffTag.PAGENUMBER, pageIndex, pageCount);

        var pixels = bitmap.Pixels;
        var rowData = new byte[width * 4];

        for (var row = 0; row < height; row++)
        {
            var rowOffset = row * width;
            for (var x = 0; x < width; x++)
            {
                var pixel = pixels[rowOffset + x];
                var dataOffset = x * 4;
                rowData[dataOffset] = pixel.Red;
                rowData[dataOffset + 1] = pixel.Green;
                rowData[dataOffset + 2] = pixel.Blue;
                rowData[dataOffset + 3] = pixel.Alpha;
            }
            tiff.WriteScanline(rowData, row);
        }

        tiff.WriteDirectory();
    }

    /// <summary>
    /// Loads a TIFF (single- or multi-page) into a layer stack. Uses LibTiff's high-level
    /// <c>ReadRGBAImageOriented</c> reader so any valid TIFF — regardless of compression,
    /// tiling, planar config, photometric interpretation or bit depth — is decoded to RGBA.
    /// </summary>
    /// <param name="filePath">The TIFF file path.</param>
    /// <param name="logger">Optional unified logger for diagnostics.</param>
    /// <returns>A new LayerStack with loaded layers, or null if nothing could be read.</returns>
    public static LayerStack? LoadLayeredTiff(string filePath, IUnifiedLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            using var tiff = Tiff.Open(filePath, "r");
            if (tiff == null)
            {
                logger?.Error(LogCategory.General, LogSource, $"LoadLayeredTiff: Tiff.Open returned null for {filePath}");
                return null;
            }

            var firstWidth = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            var firstHeight = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            var layerStack = new LayerStack(firstWidth, firstHeight);

            var pageIndex = 0;
            do
            {
                // Per-layer metadata (present on app-written TIFFs; defaults otherwise).
                var description = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
                var layerName = $"Layer {pageIndex + 1}";
                var opacity = 1.0f;
                var blendMode = BlendMode.Normal;
                var isVisible = true;
                if (description != null && description.Length > 0)
                    ParseLayerMetadata(description[0].ToString(), out layerName, out opacity, out blendMode, out isVisible);

                var bitmap = ReadCurrentPageRgba(tiff);
                if (bitmap is null)
                {
                    logger?.Warn(LogCategory.General, LogSource,
                        $"ReadRGBAImageOriented failed for page {pageIndex}; skipping page.", filePath);
                    pageIndex++;
                    continue;
                }

                var layer = new Layer(bitmap, layerName)
                {
                    Opacity = opacity,
                    BlendMode = blendMode,
                    IsVisible = isVisible
                };
                layerStack.InsertLayer(layerStack.Count, layer);

                bitmap.Dispose(); // Layer makes a copy
                pageIndex++;
            }
            while (tiff.ReadDirectory());

            if (layerStack.Count == 0)
            {
                logger?.Error(LogCategory.General, LogSource, $"LoadLayeredTiff: no readable pages in {filePath}");
                layerStack.Dispose();
                return null;
            }

            layerStack.ActiveLayer = layerStack[layerStack.Count - 1];
            logger?.Info(LogCategory.General, LogSource,
                $"Loaded TIFF with {layerStack.Count} layer(s) ({firstWidth}x{firstHeight}).", filePath);
            return layerStack;
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.General, LogSource, $"LoadLayeredTiff failed for {filePath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Decodes only the FIRST page of a TIFF into a flat RGBA bitmap. Intended for lightweight
    /// previews/thumbnails where layer structure is irrelevant — a "rough" look of the image.
    /// Returns null if the file cannot be read. Caller owns the returned bitmap.
    /// </summary>
    public static SKBitmap? DecodeFlatImage(string filePath, IUnifiedLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            using var tiff = Tiff.Open(filePath, "r");
            if (tiff == null)
            {
                logger?.Warn(LogCategory.General, LogSource, $"DecodeFlatImage: Tiff.Open returned null for {filePath}");
                return null;
            }

            var bitmap = ReadCurrentPageRgba(tiff);
            if (bitmap is null)
                logger?.Warn(LogCategory.General, LogSource, $"DecodeFlatImage: could not read first page of {filePath}");
            return bitmap;
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.General, LogSource, $"DecodeFlatImage failed for {filePath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Reads the TIFF's current directory (page) into a flat RGBA SKBitmap using LibTiff's
    /// robust high-level reader (handles any compression/tiling/photometric). The raster is
    /// top-left origin, so it maps directly to SKBitmap pixel order. Returns null on failure.
    /// </summary>
    private static SKBitmap? ReadCurrentPageRgba(Tiff tiff)
    {
        var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

        var raster = new int[width * height];
        if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT, false))
            return null;

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new SKColor[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = raster[i];
            pixels[i] = new SKColor(
                (byte)Tiff.GetR(p),
                (byte)Tiff.GetG(p),
                (byte)Tiff.GetB(p),
                (byte)Tiff.GetA(p));
        }
        bitmap.Pixels = pixels;
        return bitmap;
    }

    /// <summary>
    /// Creates metadata string for a layer.
    /// </summary>
    private static string CreateLayerMetadata(Layer layer, int index)
    {
        return $"LayerName={layer.Name}|Opacity={layer.Opacity:F2}|BlendMode={layer.BlendMode}|Visible={layer.IsVisible}|Index={index}";
    }

    /// <summary>
    /// Parses layer metadata from TIFF description.
    /// </summary>
    private static void ParseLayerMetadata(string? metadata, out string name, out float opacity, out BlendMode blendMode, out bool isVisible)
    {
        name = "Layer";
        opacity = 1.0f;
        blendMode = BlendMode.Normal;
        isVisible = true;

        if (string.IsNullOrEmpty(metadata))
            return;

        var parts = metadata.Split('|');
        foreach (var part in parts)
        {
            var keyValue = part.Split('=');
            if (keyValue.Length != 2)
                continue;

            var key = keyValue[0].Trim();
            var value = keyValue[1].Trim();

            switch (key)
            {
                case "LayerName":
                    name = value;
                    break;
                case "Opacity":
                    if (float.TryParse(value, out var op))
                        opacity = Math.Clamp(op, 0f, 1f);
                    break;
                case "BlendMode":
                    if (Enum.TryParse<BlendMode>(value, out var bm))
                        blendMode = bm;
                    break;
                case "Visible":
                    if (bool.TryParse(value, out var vis))
                        isVisible = vis;
                    break;
            }
        }
    }
}
