using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Provides TIFF file export functionality with multi-layer support.
/// Uses LibTiff.NET for proper multi-page/layered TIFF creation.
/// </summary>
public static class TiffExporter
{
    /// <summary>
    /// Saves layers to a multi-page TIFF file.
    /// Each layer is saved as a separate page with metadata.
    /// </summary>
    /// <param name="layers">The layer stack to export.</param>
    /// <param name="filePath">The output file path.</param>
    /// <returns>True if saved successfully.</returns>
    public static bool SaveLayeredTiff(LayerStack layers, string filePath)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(filePath);

        if (layers.Count == 0)
            return false;

        try
        {
            using var tiff = Tiff.Open(filePath, "w");
            if (tiff == null)
                return false;

            // Write each layer as a separate page
            for (var pageIndex = 0; pageIndex < layers.Count; pageIndex++)
            {
                var layer = layers[pageIndex];
                if (layer.Bitmap == null)
                    continue;

                var bitmap = layer.Bitmap;
                var width = bitmap.Width;
                var height = bitmap.Height;

                // Set TIFF tags for this page
                tiff.SetField(TiffTag.IMAGEWIDTH, width);
                tiff.SetField(TiffTag.IMAGELENGTH, height);
                tiff.SetField(TiffTag.SAMPLESPERPIXEL, 4); // RGBA
                tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
                tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
                tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
                tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
                tiff.SetField(TiffTag.ROWSPERSTRIP, height);
                
                // Set extra samples for alpha channel
                tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.ASSOCALPHA });

                // Store layer metadata in TIFF description
                var metadata = CreateLayerMetadata(layer, pageIndex);
                tiff.SetField(TiffTag.IMAGEDESCRIPTION, metadata);

                // Set page number (for multi-page TIFF)
                tiff.SetField(TiffTag.PAGENUMBER, pageIndex, layers.Count);

                // Get pixel data
                var pixels = bitmap.Pixels;
                var rowData = new byte[width * 4]; // RGBA

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

                // Write directory entry for this page
                tiff.WriteDirectory();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Saves a single flattened image to TIFF format.
    /// </summary>
    /// <param name="bitmap">The bitmap to save.</param>
    /// <param name="filePath">The output file path.</param>
    /// <returns>True if saved successfully.</returns>
    public static bool SaveFlattenedTiff(SKBitmap bitmap, string filePath)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            using var tiff = Tiff.Open(filePath, "w");
            if (tiff == null)
                return false;

            var width = bitmap.Width;
            var height = bitmap.Height;

            // Set TIFF tags
            tiff.SetField(TiffTag.IMAGEWIDTH, width);
            tiff.SetField(TiffTag.IMAGELENGTH, height);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 4);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
            tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
            tiff.SetField(TiffTag.ROWSPERSTRIP, height);
            tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.ASSOCALPHA });

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
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads a layered TIFF file into a layer stack.
    /// </summary>
    /// <param name="filePath">The TIFF file path.</param>
    /// <returns>A new LayerStack with loaded layers, or null if failed.</returns>
    public static LayerStack? LoadLayeredTiff(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            using var tiff = Tiff.Open(filePath, "r");
            if (tiff == null)
                return null;

            // Read first page to get dimensions
            var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

            var layerStack = new LayerStack(width, height);
            var pageIndex = 0;

            do
            {
                var pageWidth = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                var pageHeight = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
                var samplesPerPixel = tiff.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 3;

                // Read layer metadata
                var description = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
                var layerName = $"Layer {pageIndex + 1}";
                var opacity = 1.0f;
                var blendMode = BlendMode.Normal;
                var isVisible = true;

                if (description != null && description.Length > 0)
                {
                    ParseLayerMetadata(description[0].ToString(), out layerName, out opacity, out blendMode, out isVisible);
                }

                // Read pixel data
                var bitmap = new SKBitmap(pageWidth, pageHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                var pixels = new SKColor[pageWidth * pageHeight];
                var rowData = new byte[tiff.ScanlineSize()];

                for (var row = 0; row < pageHeight; row++)
                {
                    tiff.ReadScanline(rowData, row);
                    var rowOffset = row * pageWidth;

                    for (var x = 0; x < pageWidth; x++)
                    {
                        byte r, g, b, a;
                        if (samplesPerPixel >= 4)
                        {
                            var dataOffset = x * 4;
                            r = rowData[dataOffset];
                            g = rowData[dataOffset + 1];
                            b = rowData[dataOffset + 2];
                            a = rowData[dataOffset + 3];
                        }
                        else
                        {
                            var dataOffset = x * samplesPerPixel;
                            r = rowData[dataOffset];
                            g = samplesPerPixel > 1 ? rowData[dataOffset + 1] : r;
                            b = samplesPerPixel > 2 ? rowData[dataOffset + 2] : r;
                            a = 255;
                        }
                        pixels[rowOffset + x] = new SKColor(r, g, b, a);
                    }
                }

                bitmap.Pixels = pixels;

                // Create and add layer
                var layer = new Layer(bitmap, layerName)
                {
                    Opacity = opacity,
                    BlendMode = blendMode,
                    IsVisible = isVisible
                };
                layerStack.InsertLayer(0, layer); // Insert at bottom to maintain order
                
                bitmap.Dispose(); // Layer makes a copy

                pageIndex++;
            }
            while (tiff.ReadDirectory());

            // Set first layer as active
            if (layerStack.Count > 0)
            {
                layerStack.ActiveLayer = layerStack[layerStack.Count - 1];
            }

            return layerStack;
        }
        catch
        {
            return null;
        }
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
